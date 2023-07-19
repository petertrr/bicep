// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Bicep.Core.Navigation;
using Bicep.Core.Parsing;
using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
using Microsoft.WindowsAzure.ResourceStack.Common.Json;
using Newtonsoft.Json.Linq;

namespace Bicep.Core.Syntax
{
    public class TestDeclarationSyntax : StatementSyntax, ITopLevelNamedDeclarationSyntax
    {
        public TestDeclarationSyntax(IEnumerable<SyntaxBase> leadingNodes, Token keyword, IdentifierSyntax name, SyntaxBase path, SyntaxBase assignment, SyntaxBase value)
            : base(leadingNodes)
        {
            AssertKeyword(keyword, nameof(keyword), LanguageConstants.TestKeyword);
            AssertSyntaxType(path, nameof(path), typeof(StringSyntax), typeof(SkippedTriviaSyntax));
            AssertTokenType(keyword, nameof(keyword), TokenType.Identifier);
            AssertSyntaxType(assignment, nameof(assignment), typeof(Token), typeof(SkippedTriviaSyntax));
            AssertTokenType(assignment as Token, nameof(assignment), TokenType.Assignment);
            AssertSyntaxType(value, nameof(value), typeof(SkippedTriviaSyntax), typeof(ObjectSyntax));

            this.Keyword = keyword;
            this.Name = name;
            this.Path = path;
            this.Assignment = assignment;
            this.Value = value;
        }

        public Token Keyword { get; }

        public IdentifierSyntax Name { get; }

        public SyntaxBase Path { get; }

        public SyntaxBase Assignment { get; }

        public SyntaxBase Value { get; }

        public override void Accept(ISyntaxVisitor visitor) => visitor.VisitTestDeclarationSyntax(this);

        public override TextSpan Span => TextSpan.Between(this.LeadingNodes.FirstOrDefault() ?? this.Keyword, this.Value);

        public StringSyntax? TryGetPath() => Path as StringSyntax;

        public ObjectSyntax? TryGetBody() =>
            this.Value switch
            {
                ObjectSyntax @object => @object,
                SkippedTriviaSyntax => null,

                // blocked by assert in the constructor
                _ => throw new NotImplementedException($"Unexpected type of test value '{this.Value.GetType().Name}'.")
            };

        public ObjectSyntax GetBody() =>
            this.TryGetBody() ?? throw new InvalidOperationException($"A valid test body is not available on this test due to errors. Use {nameof(TryGetBody)}() instead.");

        public JToken? TryGetParameters(){
            var body = this.GetBody();
            foreach (var property in body.Properties) {
                if (property.TryGetKeyText() == "params" && property.Value is ObjectSyntax paramsObject) 
                {
                    var parameters = new JObject();
                    foreach (var parameter in paramsObject.Properties){
                        var propertyName = parameter.TryGetKeyText();
                        if (propertyName is null)
                        {
                            throw new NotImplementedException($"Error reading the parameter key");
                        }
                        else
                        {
                            switch (parameter.Value)
                            {
                                case StringSyntax stringSyntax:
                                    parameters.Add(new JProperty(propertyName, stringSyntax.TryGetLiteralValue()));
                                    break;

                                case IntegerLiteralSyntax numericSyntax:
                                    parameters[propertyName] = numericSyntax.Value;
                                    break;

                                case BooleanLiteralSyntax booleanSyntax:
                                    parameters[propertyName] = booleanSyntax.Value;
                                    break;
                                
                                case null:
                                    parameters[propertyName] = JValue.CreateNull();
                                    break;

                                case ObjectSyntax objectSyntax:
                                    parameters[propertyName] = ParseObjectSyntax(objectSyntax);
                                    break;

                                case ArraySyntax arraySyntax:
                                    parameters[propertyName] = ParseArraySyntax(arraySyntax);
                                    break;
                                default:
                                    throw new NotImplementedException($"Unexpected type of parameter value '{parameter.Value.GetType().Name}'.");
                            }
                        }
                    }
                    
                    return parameters;
                }
            }
            return null;
        }
        private static JArray ParseArraySyntax(ArraySyntax arraySyntax)
        {
            var array = new JArray();

            foreach (var item in arraySyntax.Items)
            {
                switch (item.Value)
                {
                    case StringSyntax stringSyntax:
                        array.Add(stringSyntax.TryGetLiteralValue());
                        break;

                    case IntegerLiteralSyntax numericSyntax:
                        array.Add(numericSyntax.Value);
                        break;

                    case BooleanLiteralSyntax booleanSyntax:
                        array.Add(booleanSyntax.Value);
                        break;

                    case null:
                        array.Add(JValue.CreateNull());
                        break;

                    case ObjectSyntax objectSyntax:
                        array.Add(ParseObjectSyntax(objectSyntax));
                        break;

                    case ArraySyntax nestedArraySyntax:
                        array.Add(ParseArraySyntax(nestedArraySyntax));
                        break;

                    default:
                        throw new NotImplementedException($"Unexpected type of array item '{item.GetType().Name}'.");
                }
            }

            return array;
        }

        private static JObject ParseObjectSyntax(ObjectSyntax objectSyntax)
        {
            var obj = new JObject();

            foreach (var property in objectSyntax.Properties)
            {
                var propertyName = property.TryGetKeyText();
                var propertyValue = property.Value;
                if (propertyName is null)
                {
                    throw new NotImplementedException($"Error reading the object property key");
                }
                
                switch (propertyValue)
                {
                    case StringSyntax stringSyntax:
                        obj.Add(propertyName, stringSyntax.TryGetLiteralValue());
                        break;

                    case IntegerLiteralSyntax numericSyntax:
                        obj[propertyName] = numericSyntax.Value;
                        break;

                    case BooleanLiteralSyntax booleanSyntax:
                        obj[propertyName] = booleanSyntax.Value;
                        break;

                    case null:
                        obj[propertyName] = JValue.CreateNull();
                        break;

                    case ObjectSyntax nestedObjectSyntax:
                        obj[propertyName] = ParseObjectSyntax(nestedObjectSyntax);
                        break;

                    case ArraySyntax nestedArraySyntax:
                        obj[propertyName] = ParseArraySyntax(nestedArraySyntax);
                        break;

                    default:
                        throw new NotImplementedException($"Unexpected type of object property value '{propertyValue.GetType().Name}'.");
                }
            }

            return obj;
        }


        
        



    }
}
