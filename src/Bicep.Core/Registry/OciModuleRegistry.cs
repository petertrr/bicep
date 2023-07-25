// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure;
using Bicep.Core.Configuration;
using Bicep.Core.Diagnostics;
using Bicep.Core.Features;
using Bicep.Core.FileSystem;
using Bicep.Core.Modules;
using Bicep.Core.Registry.Oci;
using Bicep.Core.Semantics;
using Bicep.Core.Tracing;
using Newtonsoft.Json;

namespace Bicep.Core.Registry
{
    public sealed class OciModuleRegistry : ExternalModuleRegistry<OciArtifactModuleReference, OciArtifactResult>
    {
        private readonly AzureContainerRegistryManager client;

        private readonly string cachePath;

        private readonly RootConfiguration configuration;

        private readonly Uri parentModuleUri;

        private const string BicepPublicMcrRegistry = "mcr.microsoft.com";

        public OciModuleRegistry(IFileResolver FileResolver, IContainerRegistryClientFactory clientFactory, IFeatureProvider features, RootConfiguration configuration, Uri parentModuleUri)
            : base(FileResolver)
        {
            this.cachePath = Path.Combine(features.CacheRootDirectory, ModuleReferenceSchemes.Oci);
            this.client = new AzureContainerRegistryManager(clientFactory);
            this.configuration = configuration;
            this.parentModuleUri = parentModuleUri;
        }

        public override string Scheme => ModuleReferenceSchemes.Oci;

        public override RegistryCapabilities GetCapabilities(OciArtifactModuleReference reference)
        {
            // cannot publish without tag
            return reference.Tag is null ? RegistryCapabilities.Default : RegistryCapabilities.Publish;
        }

        public override bool TryParseModuleReference(string? aliasName, string reference, [NotNullWhen(true)] out ModuleReference? moduleReference, [NotNullWhen(false)] out DiagnosticBuilder.ErrorBuilderDelegate? failureBuilder)
        {
            if (OciArtifactModuleReference.TryParse(aliasName, reference, configuration, parentModuleUri, out var @ref, out failureBuilder))
            {
                moduleReference = @ref;
                return true;
            }

            moduleReference = null;
            return false;
        }

        public override bool IsModuleRestoreRequired(OciArtifactModuleReference reference)
        {
            /*
             * this should be kept in sync with the WriteModuleContent() implementation
             * but beware that it's possible older versions of Bicep may be sharing this cache
             *
             * when we write content to the module cache, we attempt to get a lock so that no other writes happen in the directory
             * the code here appears to implement a lock-free read by checking existence of several files that are expected in a fully restored module
             *
             * this relies on the assumption that modules are never updated in-place in the cache
             * when we need to invalidate the cache, the module directory (or even a single file) should be deleted from the cache  //asdfg?
             */
            return
                !this.FileResolver.FileExists(this.GetModuleFileUri(reference, ModuleFileType.ModuleMain)) ||
                !this.FileResolver.FileExists(this.GetModuleFileUri(reference, ModuleFileType.Manifest)) ||
                !this.FileResolver.FileExists(this.GetModuleFileUri(reference, ModuleFileType.Metadata));
        }

        public override async Task<bool> CheckModuleExists(OciArtifactModuleReference reference)
        {
            try
            {
                // Try getting module
                await this.client.PullModuleArtifactsAsync(configuration, reference, includeSources: false);
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                // Found module but tag doesn't exist
                return false;
            }
            catch (ExternalModuleException exception) when
                (exception.InnerException is RequestFailedException &&
                ((RequestFailedException)exception.InnerException).Status == 404)
            {
                // Found no module at all
                return false;
            }
            catch (InvalidModuleException exception) when (exception.Kind == InvalidModuleExceptionKind.WrongArtifactType || exception.Kind == InvalidModuleExceptionKind.WrongModuleLayerMediaType)
            {
                throw new ExternalModuleException("An artifact with the tag already exists in the registry, but the artifact is not a Bicep file or module!", exception);
            }
            catch (RequestFailedException exception)
            {
                throw new ExternalModuleException(exception.Message, exception);
            }
            catch (Exception exception)
            {
                throw new ExternalModuleException(exception.Message, exception);
            }

            return true;
        }

        public override bool TryGetLocalModuleEntryPointUri(OciArtifactModuleReference reference, [NotNullWhen(true)] out Uri? localUri, [NotNullWhen(false)] out DiagnosticBuilder.ErrorBuilderDelegate? failureBuilder)
        {
            failureBuilder = null;
            localUri = this.GetModuleFileUri(reference, ModuleFileType.ModuleMain);
            return true;
        }

        public override string? TryGetDocumentationUri(OciArtifactModuleReference ociArtifactModuleReference)
        {
            var ociAnnotations = TryGetOciAnnotations(ociArtifactModuleReference);
            if (ociAnnotations is null ||
                !ociAnnotations.TryGetValue(LanguageConstants.OciOpenContainerImageDocumentationAnnotation, out string? documentationUri)
                || string.IsNullOrWhiteSpace(documentationUri))
            {
                // Automatically generate a help URI for public MCR modules
                if (ociArtifactModuleReference.Registry == BicepPublicMcrRegistry && ociArtifactModuleReference.Repository.StartsWith(LanguageConstants.McrRepositoryPrefix, StringComparison.Ordinal))
                {
                    var moduleName = ociArtifactModuleReference.Repository.Substring(LanguageConstants.McrRepositoryPrefix.Length);
                    return ociArtifactModuleReference.Tag is null ? null : GetPublicBicepModuleDocumentationUri(moduleName, ociArtifactModuleReference.Tag);
                }

                return null;
            }

            return documentationUri;
        }

        /// <summary>
        /// Automatically generate a help URI for public MCR modules
        /// </summary>
        /// <param name="publicModuleName">e.g. app/dapr-containerapp</param>
        /// <param name="tag">e.g. 1.0.1</param>
        public static string GetPublicBicepModuleDocumentationUri(string publicModuleName, string tag) //asdfg show doc link in sources?
        {
            // Example: https://github.com/Azure/bicep-registry-modules/tree/app/dapr-containerapp/1.0.2/modules/app/dapr-containerapp/README.md
            return $"https://github.com/Azure/bicep-registry-modules/tree/{publicModuleName}/{tag}/modules/{publicModuleName}/README.md";
        }

        public override Task<string?> TryGetDescription(OciArtifactModuleReference ociArtifactModuleReference)
        {
            var ociAnnotations = TryGetOciAnnotations(ociArtifactModuleReference);
            return Task.FromResult(DescriptionHelper.TryGetFromOciManifestAnnotations(ociAnnotations));
        }

        private ImmutableDictionary<string, string>? TryGetOciAnnotations(OciArtifactModuleReference ociArtifactModuleReference)
        {
            try
            {
                string manifestFilePath = this.GetModuleFilePath(ociArtifactModuleReference, ModuleFileType.Manifest);
                if (!File.Exists(manifestFilePath))
                {
                    return null;
                }

                string manifestFileContents = File.ReadAllText(manifestFilePath);
                if (string.IsNullOrWhiteSpace(manifestFileContents))
                {
                    return null;
                }

                OciManifest? ociManifest = JsonConvert.DeserializeObject<OciManifest>(manifestFileContents);
                if (ociManifest is null)
                {
                    return null;
                }

                return ociManifest.Annotations;
            }
            catch
            {
                return null;
            }
        }

        public override async Task<IDictionary<ModuleReference, DiagnosticBuilder.ErrorBuilderDelegate>> RestoreModules(IEnumerable<OciArtifactModuleReference> references)
        {
            var statuses = new Dictionary<ModuleReference, DiagnosticBuilder.ErrorBuilderDelegate>();

            foreach (var reference in references)
            {
                using var timer = new ExecutionTimer($"Restore module {reference.FullyQualifiedReference}");
                var (result, errorMessage) = await this.TryPullArtifactAsync(configuration, reference);

                if (result is null)
                {
                    if (errorMessage is not null)
                    {
                        statuses.Add(reference, x => x.ModuleRestoreFailedWithMessage(reference.FullyQualifiedReference, errorMessage));
                        timer.OnFail(errorMessage);
                    }
                    else
                    {
                        statuses.Add(reference, x => x.ModuleRestoreFailed(reference.FullyQualifiedReference));
                        timer.OnFail();
                    }
                }
            }

            return statuses;
        }

        public override async Task<IDictionary<ModuleReference, DiagnosticBuilder.ErrorBuilderDelegate>> InvalidateModulesCache(IEnumerable<OciArtifactModuleReference> references)
        {
            return await base.InvalidateModulesCacheInternal(references);
        }

        public override async Task PublishModule(OciArtifactModuleReference moduleReference, Stream compiledArmTemplate, Stream? bicepSources/*asdfg*/, string? documentationUri, string? description) //asdfg
        {
            var config = new StreamDescriptor(Stream.Null, BicepMediaTypes.BicepModuleConfigV1); //asdfg
            var layer = new StreamDescriptor(compiledArmTemplate, BicepMediaTypes.BicepModuleLayerV1Json);

            try
            {
                await this.client.PushModuleArtifactsAsync(configuration, moduleReference, BicepMediaTypes.BicepModuleArtifactType, config, bicepSources, documentationUri, description, layer);
            }
            catch (AggregateException exception) when (CheckAllInnerExceptionsAreRequestFailures(exception))
            {
                // will include several retry messages, but likely the best we can do
                throw new ExternalModuleException(exception.Message, exception);
            }
            catch (RequestFailedException exception)
            {
                // can only happen if client retries are disabled
                throw new ExternalModuleException(exception.Message, exception);
            }
        }

        // Writes the contents of the downloaded module into the local cache
        protected override void WriteModuleContentToCache(OciArtifactModuleReference reference, OciArtifactResult result)
        {
            /*
             * this should be kept in sync with the IsModuleRestoreRequired() implementation
             * but beware that it's possible older versions of Bicep may be sharing this cache
             */

            // write main.json
            this.FileResolver.Write(this.GetModuleFileUri(reference, ModuleFileType.ModuleMain), result.ModuleStream);

            // write manifest
            // it's important to write the original stream here rather than serialize the manifest object
            // this way we guarantee the manifest hash will match
            this.FileResolver.Write(this.GetModuleFileUri(reference, ModuleFileType.Manifest), result.ManifestStream);

            // write metadata
            var metadata = new ModuleMetadata(result.ManifestDigest);
            using var metadataStream = new MemoryStream();
            OciSerialization.Serialize(metadataStream, metadata);
            metadataStream.Position = 0;
            this.FileResolver.Write(this.GetModuleFileUri(reference, ModuleFileType.Metadata), metadataStream);

            // write sources
            var sourcesFile = this.GetModuleFileUri(reference, ModuleFileType.Sources);
            if (result.SourcesStream is Stream sourcesStream)
            {
                this.FileResolver.Write(sourcesFile, result.SourcesStream);
            }
            else
            {
                this.FileResolver.DeleteFileIfExists(sourcesFile);
             }
        }

        protected override string GetModuleDirectoryPath(OciArtifactModuleReference reference)
        {
            // cachePath is already set to %userprofile%\.bicep\br or ~/.bicep/br by default depending on OS
            // we need to split each component of the reference into a sub directory to fit within the max file name length limit on linux and mac

            // TODO: Need to deal with IDNs (internationalized domain names)
            // domain names can only contain alphanumeric chars, _, -, and numbers (example.azurecr.io or example.azurecr.io:443)
            // IPV4 addresses only contain dots and numbers (127.0.0.1 or 127.0.0.1:5000)
            // IPV6 addresses are hex digits with colons (2001:db8:3333:4444:CCCC:DDDD:EEEE:FFFF or [2001:db8:3333:4444:CCCC:DDDD:EEEE:FFFF]:5000)
            // the only problematic character is the colon, which we will replace with $ which is not allowed in any of the possible registry values
            // we will also normalize casing for registries since they are case-insensitive
            var registry = reference.Registry.Replace(':', '$').ToLowerInvariant();

            // modules can have mixed hierarchy depths so we will flatten a repository into a single directory name
            // however to do this we must get rid of slashes (not a valid file system char on windows and a directory separator on linux/mac)
            // the replacement char must one that is not valid in a repository string but is valid in common type systems
            var repository = reference.Repository.Replace('/', '$');

            string? tagOrDigest;
            if (reference.Tag is not null)
            {
                // tags are case-sensitive with length up to 128
                tagOrDigest = TagEncoder.Encode(reference.Tag);
            }
            else if (reference.Digest is not null)
            {
                // digests are strings like "sha256:e207a69d02b3de40d48ede9fd208d80441a9e590a83a0bc915d46244c03310d4"
                // and are already guaranteed to be lowercase
                // the only problematic character is the : which we will replace with a #
                // however the encoding we choose must not be ambiguous with the tag encoding
                tagOrDigest = reference.Digest.Replace(':', '#');
            }
            else
            {
                throw new InvalidOperationException("Module reference is missing both tag and digest.");
            }

            //var packageDir = WebUtility.UrlEncode(reference.UnqualifiedReference);
            return Path.Combine(this.cachePath, registry, repository, tagOrDigest);
        }

        protected override Uri GetModuleLockFileUri(OciArtifactModuleReference reference) => this.GetModuleFileUri(reference, ModuleFileType.Lock);

        private async Task<(OciArtifactResult?, string? errorMessage)> TryPullArtifactAsync(RootConfiguration configuration, OciArtifactModuleReference reference)
        {
            try
            {
                var result = await this.client.PullModuleArtifactsAsync(configuration, reference);

                await this.TryWriteModuleContentToCacheAsync(reference, result);

                return (result, null);
            }
            catch (ExternalModuleException exception)
            {
                // we can trust the message in this exception
                return (null, exception.Message);
            }
            catch (AggregateException exception) when (CheckAllInnerExceptionsAreRequestFailures(exception))
            {
                // the message on this one is not great because it includes all the retry attempts
                // however, we don't really have a good way to classify them in a cross-platform way
                return (null, exception.Message);
            }
            catch (RequestFailedException exception)
            {
                // this can only happen if we disable retry on the client and a registry request failed
                return (null, exception.Message);
            }
            catch (Exception exception)
            {
                return (null, $"Unhandled exception: {exception}");
            }
        }

        private static bool CheckAllInnerExceptionsAreRequestFailures(AggregateException exception) =>
            exception.InnerExceptions.All(inner => inner is RequestFailedException);

        private Uri GetModuleFileUri(OciArtifactModuleReference reference, ModuleFileType fileType)
        {
            string localFilePath = this.GetModuleFilePath(reference, fileType);
            if (Uri.TryCreate(localFilePath, UriKind.Absolute, out var uri))
            {
                return uri;
            }

            throw new NotImplementedException($"Local module file path is malformed: \"{localFilePath}\"");
        }

        private string GetModuleFilePath(OciArtifactModuleReference reference, ModuleFileType fileType)
        {
            var fileName = fileType switch
            {
                ModuleFileType.ModuleMain => "main.json",
                ModuleFileType.Lock => "lock",
                ModuleFileType.Manifest => "manifest",
                ModuleFileType.Metadata => "metadata",
                ModuleFileType.Sources => "sources.zip",
                _ => throw new NotImplementedException($"Unexpected module file type '{fileType}'.")
            };

            return Path.Combine(this.GetModuleDirectoryPath(reference), fileName);
        }

        public override SourceArchive? TryGetSources(OciArtifactModuleReference reference)
        {
            var zipFile = GetModuleFilePath(reference, ModuleFileType.Sources);
            if (!File.Exists(zipFile))
            {
                return null;
            }

            var stream = File.OpenRead(zipFile);
            return new SourceArchive(stream);
        }

        private enum ModuleFileType
        {
            ModuleMain,
            Manifest,
            Lock,
            Metadata,
            Sources,
        };
    }
}
