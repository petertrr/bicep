// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography.Xml;
using System.Security.Policy;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Containers.ContainerRegistry;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.ResourceManager;
using Bicep.Core.Configuration;
using Bicep.Core.Extensions;
using Bicep.Core.Modules;
using Bicep.Core.Registry.Auth;
using Bicep.Core.Registry.Oci;
using Bicep.Core.Tracing;
using Microsoft.Identity.Client.Platforms.Features.DesktopOs.Kerberos;
using Microsoft.WindowsAzure.ResourceStack.Common.Extensions;
using OciDescriptor = Bicep.Core.Registry.Oci.OciDescriptor;
using OciManifest = Bicep.Core.Registry.Oci.OciManifest;

namespace Bicep.Core.Registry
{
    public class AzureContainerRegistryManager
    {
        private record ReferrersResponse(
            int SchemaVersion,
            string MediaType,
            OciDescriptor[] Manifests);

        // media types are case-insensitive (they are lowercase by convention only)
        private const StringComparison MediaTypeComparison = StringComparison.OrdinalIgnoreCase;
        private const StringComparison DigestComparison = StringComparison.Ordinal;

        private readonly IContainerRegistryClientFactory clientFactory;

        public AzureContainerRegistryManager(IContainerRegistryClientFactory clientFactory)
        {
            this.clientFactory = clientFactory;
        }

        [SuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "Relying on references to required properties of the generic type elsewhere in the codebase.")]
        public async Task<OciArtifactResult> PullArtifactAsync(RootConfiguration configuration, OciArtifactModuleReference moduleReference)
        {
            ContainerRegistryContentClient client;
            OciManifest manifest;
            Stream manifestStream;
            string manifestDigest;

            async Task<(ContainerRegistryContentClient, OciManifest, Stream, string)> DownloadManifestInternalAsync(bool anonymousAccess)//asdfg
            {
                var client = this.CreateBlobClient(configuration, moduleReference, anonymousAccess);
                var (manifest, manifestStream, manifestDigest) = await DownloadManifestAsync(moduleReference, client);
                return (client, manifest, manifestStream, manifestDigest);
            }

            try
            {
                // Try authenticated client first.
                Trace.WriteLine($"Authenticated attempt to pull artifact for module {moduleReference.FullyQualifiedReference}.");
                (client, manifest, manifestStream, manifestDigest) = await DownloadManifestInternalAsync(anonymousAccess: false);
            }
            catch (RequestFailedException exception) when (exception.Status == 401 || exception.Status == 403)
            {
                // Fall back to anonymous client.
                Trace.WriteLine($"Authenticated attempt to pull artifact for module {moduleReference.FullyQualifiedReference} failed, received code {exception.Status}. Fallback to anonymous pull.");
                (client, manifest, manifestStream, manifestDigest) = await DownloadManifestInternalAsync(anonymousAccess: true);
            }
            catch (CredentialUnavailableException)
            {
                // Fall back to anonymous client.
                Trace.WriteLine($"Authenticated attempt to pull artifact for module {moduleReference.FullyQualifiedReference} failed due to missing login step. Fallback to anonymous pull.");
                (client, manifest, manifestStream, manifestDigest) = await DownloadManifestInternalAsync(anonymousAccess: true);
            }

            var moduleStream = await ProcessManifest(client, manifest);

            Stream? sourcesStream = null;



            //using var httpClient = new HttpClient();

            //GET https://sawbicep.azurecr.io/oauth2/token?scope=repository%3Astorage%3Apull&service=sawbicep.azurecr.io HTTP/1.1


            //var httpClient = await clientFactory.CreateAuthenticatedHttpClientAsync(configuration);
            // //UriBuilder uri = new UriBuilder(GetRegistryUri(moduleReference));
            // //https://mcr.microsoft.com/v2/bicep/app/app-configuration/referrers/sha256:0000000000000000000000000000000000000000000000000000000000000000
            //var uri = $"{GetRegistryUri(moduleReference)}/v2/{moduleReference.Repository}/referrers/sha256:0000000000000000000000000000000000000000000000000000000000000000";
            //var a = httpClient.GetAsync(uri);
            var r = client.Pipeline.CreateRequest();
            r.Method = new RequestMethod("GET");
            r.Content = "hi";
            var request = client.Pipeline.CreateRequest();
            request.Method = RequestMethod.Get;

            request.Uri.Reset(GetRegistryUri(moduleReference));
            request.Uri.AppendPath("/v2/", false);
            request.Uri.AppendPath(moduleReference.Repository, true);
            request.Uri.AppendPath("/referrers/", false);
            request.Uri.AppendPath(manifestDigest);

            //request.Uri.Reset(new Uri(uri, UriKind.Absolute));
            using var cts = new CancellationTokenSource();
            var response = await client.Pipeline.SendRequestAsync(request, cts.Token);

            if (!response.IsError)
            {
                /* Example:
                    {
                      "schemaVersion": 2,
                      "mediaType": "application/vnd.oci.image.index.v1+json",
                      "manifests": [
                        {
                          "mediaType": "application/vnd.oci.image.manifest.v1+json",
                          "digest": "sha256:210a9f9e8134fc77940ea17f971adcf8752e36b513eb7982223caa1120774284",
                          "size": 811,
                          "artifactType": "application/vnd.ms.bicep.module.sources"
                        },
                        ...
                */

                //var referrersResponse = JsonSerializer.Deserialize<ReferrersResponse>(response.Content.ToString(), new JsonSerializerOptions() { PropertyNameCaseInsensitive = true });
                JsonElement referrersResponse =  JsonSerializer.Deserialize<JsonElement>(response.Content.ToString());
                //referrersResponse.
                //var bicepSourcesDigests = referrersResponse?.Manifests.Where(m => m.ArtifactType == BicepMediaTypes.BicepModuleSourcesArtifactType).Select(m => m.Digest);
                var bicepSourcesManifestDigests = referrersResponse.TryGetPropertyByPath("manifests")?.AsList().
                    Where(m => m.GetProperty("artifactType").As<string>() == BicepMediaTypes.BicepModuleSourcesArtifactType)
                    .Select(m => m.GetProperty("digest").As<string>());

                //object? body = response.Content.ToObjectFromJson();
                //var manifests = body.As<Dictionary<string, object>>()?["manifests"]?.AsArray();
                //var bicepSourcesManifests = manifests?.Where(m => m.As<Dictionary<string, object>>()?["artifactType"].As<string>() == BicepMediaTypes.BicepModuleSourcesArtifactType);
                if (bicepSourcesManifestDigests?.Count() > 1)
                {
                    throw new Exception("asdfg");
                }
                else if (bicepSourcesManifestDigests?.SingleOrDefault() is string sourcesManifestDigest)
                {
                    var sourcesManifest = await client.GetManifestAsync(sourcesManifestDigest, cts.Token/*asdfg*/);
                    var sourcesBlobDigest = sourcesManifest.Value.Digest;
                    var sourcesBlobResult = await client.DownloadBlobContentAsync(sourcesBlobDigest, cts.Token/*asdfg*/);
                    sourcesStream = sourcesBlobResult.Value.Content.ToStream();
                }

                //if (bicepSourcesManifests?.FirstOrDefault() is object sourcesManifest ) {
                //    if (sourcesManifest.As<Dictionary<string, object>>()?["digest"]?.As<string>() is string sourcesManifestDigest) {
                //        var sourcesManifest = client.GetManifestAsync(sourcesManifestDigest);
                //    }
                //}

                //var a = body;
                //var b = a;

            }
            else
            {
                throw new Exception($"Request failed with status code {response.Status}");
            }

            // HttpPipeline pipeline = new HttpPipeline(
            //     new HttpClientTransport());
            // pipeline.
            //new TokenCredentialAuthenticationPolicy(credential, "https://management.azure.com/.default"),
            // new HttpLoggingPolicy(),
            // new RetryPolicy(),
            //new BearerTokenAuthenticationPolicy(credential, "https://management.azure.com/.default"));

            // // Create the ARM client with the custom HttpPipeline
            // ArmClient armClient = new ArmClient(new DefaultAzureCredential(), pipeline);


            //var credential = tokenCredentialFactory.CreateChain(rootConfiguration.Cloud.CredentialPrecedence, rootConfiguration.Cloud.ActiveDirectoryAuthorityUri);

            //var options = new ArmClientOptions();
            //options.Diagnostics.ApplySharedResourceManagerSettings();
            //options.Environment = new ArmEnvironment(rootConfiguration.Cloud.ResourceManagerEndpointUri, rootConfiguration.Cloud.AuthenticationScope);

            //return new ArmClient(credential);




            //var options = new ArmClientOptions();
            //options.Diagnostics.ApplySharedResourceManagerSettings();
            //options.Environment = new ArmEnvironment(configuration.Cloud.ResourceManagerEndpointUri, configuration.Cloud.AuthenticationScope);
            //foreach (var (resourceType, apiVersion) in resourceTypeApiVersionMapping)
            //{
            //    options.SetApiVersion(new ResourceType(resourceType), apiVersion);
            //}

            //var credential = this.credentialFactory.CreateChain(configuration.Cloud.CredentialPrecedence, configuration.Cloud.ActiveDirectoryAuthorityUri);

            //return new ArmClient(credential, subscriptionId, options);



            //asdfg??s
            //var options = new ContainerRegistryClientOptions();
            //options.Diagnostics.ApplySharedContainerRegistrySettings();
            //options.Audience = new ContainerRegistryAudience(configuration.Cloud.ResourceManagerAudience);

            ////asdfg
            //var credential = this.credentialFactory.CreateChain(configuration.Cloud.CredentialPrecedence, configuration.Cloud.ActiveDirectoryAuthorityUri);


            //string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
            //client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            //((RequestUriBuilder)rawRequestUriBuilder).AppendPath("/v2/", false);
            //((RequestUriBuilder)rawRequestUriBuilder).AppendPath(name, true);
            //((RequestUriBuilder)rawRequestUriBuilder).AppendPath("/manifests/", false);
            //((RequestUriBuilder)rawRequestUriBuilder).AppendPath(reference, true);
            //HttpResponseMessage response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseContentRead/*asdfg, cancellationToken*/);

            //if (response.IsSuccessStatusCode)
            //{
            //    // Read the response content as a string
            //    string responseBody = await response.Content.ReadAsStringAsync();

            //    // Do something with the response
            //    //asdfg Console.WriteLine(responseBody);
            //}
            //else
            //{
            //    //asdfg Console.WriteLine($"Request failed with status code {response.StatusCode}");
            //}




            return new OciArtifactResult(manifestDigest, manifest, manifestStream, moduleStream, sourcesStream);
        }

        //asdfg https://learn.microsoft.com/en-us/dotnet/api/overview/azure/containers.containerregistry-readme?view=azure-dotnet#upload-images
        //asdfg https://learn.microsoft.com/en-us/azure/container-registry/container-registry-image-formats#oci-artifacts
        // asdfg Azure Container Registry supports the OCI Distribution Specification, a vendor-neutral, cloud-agnostic spec to store, share, secure, and deploy container images and other content types (artifacts). The specification allows a registry to store a wide range of artifacts in addition to container images. You use tooling appropriate to the artifact to push and pull artifacts. For examples, see:
        //asdfg https://github.com/oras-project/artifacts-spec/blob/main/scenarios.md


        public async Task PushArtifactAsync(RootConfiguration configuration, OciArtifactModuleReference moduleReference, string? artifactType, StreamDescriptor config, Stream? bicepSources/*asdfg dono't pass this in*/, string? documentationUri = null, string? description = null, params StreamDescriptor[] layers)
        {
            // TODO: How do we choose this? Does it ever change?
            var algorithmIdentifier = DescriptorFactory.AlgorithmIdentifierSha256;

            // push is not supported anonymously
            var blobClient = this.CreateBlobClient(configuration, moduleReference, anonymousAccess: false);

            config.ResetStream();
            var configDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, config);

            config.ResetStream();
            var result1 = await blobClient.UploadBlobAsync(config.Stream);//asdfg

            var layerDescriptors = new List<OciDescriptor>(layers.Length);
            foreach (var layer in layers)
            {
                layer.ResetStream();
                var layerDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, layer);
                layerDescriptors.Add(layerDescriptor);

                layer.ResetStream();
                var result2 = await blobClient.UploadBlobAsync(layer.Stream);//asdfg
            }

            var annotations = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(documentationUri))
            {
                annotations[LanguageConstants.OciOpenContainerImageDocumentationAnnotation] = documentationUri;
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                annotations[LanguageConstants.OciOpenContainerImageDescriptionAnnotation] = description;
            }

            // This is important to ensure any sources manifests will always point to a unique module manifest,
            //   even if something in the sources changes that doesn't affect the compiled output.
            annotations[LanguageConstants.OciOpenContainerImageCreatedAnnotation] = DateTime.UtcNow.ToRFC3339();

            var manifest = new OciManifest(2, null, artifactType, configDescriptor, layerDescriptors, null, annotations);

            using var manifestStream = new MemoryStream();
            OciSerialization.Serialize(manifestStream, manifest);

            manifestStream.Position = 0;
            var manifestBinaryData = await BinaryData.FromStreamAsync(manifestStream);
            var manifestUploadResult = await blobClient.SetManifestAsync(manifestBinaryData, moduleReference.Tag, mediaType: ManifestMediaType.OciImageManifest);

            manifestStream.Position = 0;
            var manifestStreamDescriptor = new StreamDescriptor(manifestStream, ManifestMediaType.OciImageManifest.ToString()/*asdfg*/); //asdfg
            var manifestDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, manifestStreamDescriptor);

            if (bicepSources is not null)
            {
                // asdfg remove current attachments (only if force??)

                // NOTE: Azure Container Registries won't recognize this as a valid attachment with this being valid JSON, so write out an empty object
                using var innerConfigStream = new MemoryStream(new byte[] { (byte)'{', (byte)'}' });
                var configasdfg = new StreamDescriptor(innerConfigStream, BicepMediaTypes.BicepModuleSourcesArtifactType);//, new Dictionary<string, string> { { "asdfg1", "asdfg value" } });
                var configasdfgDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, configasdfg);

                // Upload config blob
                configasdfg.ResetStream();
                var asdfgresponse1 = await blobClient.UploadBlobAsync(configasdfg.Stream); // asdfg should get digest from result
                var layerasdfg = new StreamDescriptor(bicepSources, BicepMediaTypes.BicepModuleSourcesV1Layer, new Dictionary<string, string> { { "org.opencontainers.image.title", $"Sources for {moduleReference.FullyQualifiedReference}"/*asdfg*/ } });
                layerasdfg.ResetStream();
                var layerasdfgDescriptor = DescriptorFactory.CreateDescriptor(algorithmIdentifier, layerasdfg);

                layerasdfg.ResetStream();
                var asdfgresponse2 = await blobClient.UploadBlobAsync(layerasdfg.Stream);

                var manifestasdfg = new OciManifest(
                    2,
                    null,
                    BicepMediaTypes.BicepModuleSourcesArtifactType,
                    configasdfgDescriptor,
                    new List<OciDescriptor> { layerasdfgDescriptor },
                    subject: manifestDescriptor, // This is the reference back to the main manifest that links the source manifest to it
                    new Dictionary<string, string> { { LanguageConstants.OciOpenContainerImageCreatedAnnotation, DateTime.UtcNow.ToRFC3339() } }
                    );

                using var manifestasdfgStream = new MemoryStream();
                OciSerialization.Serialize(manifestasdfgStream, manifestasdfg);

                manifestasdfgStream.Position = 0;
                var manifestasdfgBinaryData = await BinaryData.FromStreamAsync(manifestasdfgStream);
                var manifestasdfgUploadResult = await blobClient.SetManifestAsync(manifestasdfgBinaryData, null);//, mediaType: ManifestMediaType.OciImageManifest/*asdfg?*/);
            }
        }

        private static Uri GetRegistryUri(OciArtifactModuleReference moduleReference) => new($"https://{moduleReference.Registry}");

        private ContainerRegistryContentClient CreateBlobClient(RootConfiguration configuration, OciArtifactModuleReference moduleReference, bool anonymousAccess) => anonymousAccess
            ? this.clientFactory.CreateAnonymousBlobClient(configuration, GetRegistryUri(moduleReference), moduleReference.Repository)
            : this.clientFactory.CreateAuthenticatedBlobClient(configuration, GetRegistryUri(moduleReference), moduleReference.Repository);

        private static async Task<(OciManifest, Stream, string)> DownloadManifestAsync(OciArtifactModuleReference moduleReference, ContainerRegistryContentClient client)
        {
            Response<GetManifestResult> manifestResponse;
            try
            {
                // either Tag or Digest is null (enforced by reference parser)
                var tagOrDigest = moduleReference.Tag
                    ?? moduleReference.Digest
                    ?? throw new ArgumentNullException(nameof(moduleReference), $"The specified module reference has both {nameof(moduleReference.Tag)} and {nameof(moduleReference.Digest)} set to null.");

                manifestResponse = await client.GetManifestAsync(tagOrDigest);
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                // manifest does not exist
                throw new OciModuleRegistryException("The module does not exist in the registry.", exception);
            }

            // the Value is disposable, but we are not calling it because we need to pass the stream outside of this scope
            var stream = manifestResponse.Value.Manifest.ToStream();

            // BUG: The SDK internally consumed the stream for validation purposes and left position at the end
            stream.Position = 0;
            ValidateManifestResponse(manifestResponse);

            // the SDK doesn't expose all the manifest properties we need
            // so we need to deserialize the manifest ourselves to get everything
            stream.Position = 0;
            var deserialized = DeserializeManifest(stream);
            stream.Position = 0;

            return (deserialized, stream, manifestResponse.Value.Digest);
        }

        private static void ValidateManifestResponse(Response<GetManifestResult> manifestResponse)
        {
            var digestFromRegistry = manifestResponse.Value.Digest;
            var stream = manifestResponse.Value.Manifest.ToStream();

            // TODO: The registry may use a different digest algorithm - we need to handle that
            string digestFromContent = DescriptorFactory.ComputeDigest(DescriptorFactory.AlgorithmIdentifierSha256, stream);

            if (!string.Equals(digestFromRegistry, digestFromContent, DigestComparison))
            {
                throw new OciModuleRegistryException($"There is a mismatch in the manifest digests. Received content digest = {digestFromContent}, Digest in registry response = {digestFromRegistry}");
            }
        }

        private static async Task<Stream> ProcessManifest(ContainerRegistryContentClient client, OciManifest manifest)
        {
            // Bicep versions before 0.14 used to publish modules without the artifactType field set in the OCI manifest,
            // so we must allow null here
            if (manifest.ArtifactType is not null && !string.Equals(manifest.ArtifactType, BicepMediaTypes.BicepModuleArtifactType, MediaTypeComparison))
            {
                throw new InvalidModuleException($"Expected OCI artifact to have the artifactType field set to either null or '{BicepMediaTypes.BicepModuleArtifactType}' but found '{manifest.ArtifactType}'.", InvalidModuleExceptionKind.WrongArtifactType);
            }

            ProcessConfig(manifest.Config);
            if (manifest.Layers.Length != 1)
            {
                throw new InvalidModuleException("Expected a single layer in the OCI artifact.");
            }

            var layer = manifest.Layers.Single();

            return await ProcessLayer(client, layer);
        }

        private static void ValidateBlobResponse(Response<DownloadRegistryBlobResult> blobResponse, OciDescriptor descriptor)
        {
            var stream = blobResponse.Value.Content.ToStream();

            if (descriptor.Size != stream.Length)
            {
                throw new InvalidModuleException($"Expected blob size of {descriptor.Size} bytes but received {stream.Length} bytes from the registry.");
            }

            stream.Position = 0;
            string digestFromContents = DescriptorFactory.ComputeDigest(DescriptorFactory.AlgorithmIdentifierSha256, stream);
            stream.Position = 0;

            if (!string.Equals(descriptor.Digest, digestFromContents, DigestComparison))
            {
                throw new InvalidModuleException($"There is a mismatch in the layer digests. Received content digest = {digestFromContents}, Requested digest = {descriptor.Digest}");
            }
        }

        private static async Task<Stream> ProcessLayer(ContainerRegistryContentClient client, OciDescriptor layer)
        {
            if (!string.Equals(layer.MediaType, BicepMediaTypes.BicepModuleLayerV1Json, MediaTypeComparison))
            {
                throw new InvalidModuleException($"Did not expect layer media type \"{layer.MediaType}\".", InvalidModuleExceptionKind.WrongModuleLayerMediaType);
            }

            Response<DownloadRegistryBlobResult> blobResult;
            try
            {
                blobResult = await client.DownloadBlobContentAsync(layer.Digest);
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                throw new InvalidModuleException($"Module manifest refers to a non-existent blob with digest \"{layer.Digest}\".", exception);
            }

            ValidateBlobResponse(blobResult, layer);

            return blobResult.Value.Content.ToStream();
        }

        private static void ProcessConfig(OciDescriptor config)
        {
            // media types are case insensitive
            if (!string.Equals(config.MediaType, BicepMediaTypes.BicepModuleConfigV1, MediaTypeComparison))
            {
                throw new InvalidModuleException($"Did not expect config media type \"{config.MediaType}\".");
            }

            if (config.Size != 0)
            {
                throw new InvalidModuleException("Expected an empty config blob.");
            }
        }

        private static OciManifest DeserializeManifest(Stream stream)
        {
            try
            {
                return OciSerialization.Deserialize<OciManifest>(stream);
            }
            catch (Exception exception)
            {
                throw new InvalidModuleException("Unable to deserialize the module manifest.", exception);
            }
        }
    }
}
