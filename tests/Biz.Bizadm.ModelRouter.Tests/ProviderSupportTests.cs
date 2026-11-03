using System.Configuration;
using System.Net;
using System.Text;
using Biz.Bizadm.ModelRouter.Server;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Core;

namespace Biz.Bizadm.ModelRouter.Tests;

[TestClass]
public sealed class ProviderSupportTests
{
    [TestMethod]
    public void DeserializeConfiguration_ProviderDefaultsToAzureFoundry()
    {
        Configuration configuration = DeserializeConfiguration(CreateYaml());

        Assert.AreEqual(Provider.AzureFoundry, configuration.OpenAI.Provider);
    }

    [TestMethod]
    [DataRow("AzureFoundry", Provider.AzureFoundry)]
    [DataRow("OpenAI", Provider.OpenAI)]
    public void DeserializeConfiguration_ParsesProvider(string provider, Provider expected)
    {
        Configuration configuration = DeserializeConfiguration(CreateYaml($"Provider: {provider}\n"));

        Assert.AreEqual(expected, configuration.OpenAI.Provider);
    }

    [TestMethod]
    public void DeserializeConfiguration_RejectsUnknownProvider()
    {
        Assert.ThrowsExactly<YamlException>(() => DeserializeConfiguration(CreateYaml("Provider: Unknown\n")));
    }

    [TestMethod]
    public void Validate_RejectsUndefinedProviderValue()
    {
        Configuration.OpenAIConfiguration configuration = new() { Provider = (Provider)99 };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [TestMethod]
    [DataRow(Provider.AzureFoundry, "https://foundry.example/openai", "test-key", null)]
    [DataRow(Provider.OpenAI, "https://api.openai.com/v1", null, "Bearer test-key")]
    public async Task ProviderAuthentication_IsUsedByAllForwardingPaths(
        Provider provider,
        string endpoint,
        string? expectedApiKey,
        string? expectedAuthorization)
    {
        List<HttpRequestMessage> requests = new();
        using HttpClient httpClient = new(new CaptureRequestHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }));

        Configuration configuration = new()
        {
            OpenAI = new()
            {
                Provider = provider,
                OpenAIEndpoint = endpoint,
                OpenAIKey = "test-key"
            },
            ReasoningByModel = new Dictionary<string, ModelReasoningOptions>
            {
                ["m"] = new()
                {
                    Effort = "medium",
                    ChatToolsMode = "BridgeToResponses",
                    SupportsNone = true
                }
            }
        };
        Program.ConfigureProviderAuthentication(httpClient, configuration);
        AzureFoundryChatClient client = new(
            httpClient,
            configuration,
            NullLogger<AzureFoundryChatClient>.Instance);

        await DisposeResponseAsync(await client.ForwardChatCompletionsAsync(
            JsonBody("{\"model\":\"m\",\"messages\":[]}"), "application/json", CancellationToken.None));
        await DisposeResponseAsync(await client.ForwardResponsesAsync(
            JsonBody("{\"model\":\"m\",\"input\":\"hello\"}"), "application/json", CancellationToken.None));
        await DisposeResponseAsync(await client.ForwardModelsAsync(CancellationToken.None));
        await DisposeResponseAsync(await client.ForwardModelAsync("model name", CancellationToken.None));
        await DisposeResponseAsync(await client.ForwardChatCompletionsAsync(
            JsonBody("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"f\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}]}"),
            "application/json",
            CancellationToken.None));

        Assert.AreEqual(5, requests.Count);
        CollectionAssert.AreEqual(
            new[] { "chat/completions", "responses", "models", "models/model%20name", "responses" },
            requests.Select(request => request.RequestUri!.AbsolutePath[(request.RequestUri.AbsolutePath.LastIndexOf("/v1/", StringComparison.Ordinal) + 4)..]).ToArray());

        foreach (HttpRequestMessage request in requests)
        {
            Assert.AreEqual(expectedApiKey, request.Headers.TryGetValues("api-key", out IEnumerable<string>? apiKey)
                ? Assert.ContainsSingle(apiKey)
                : null);
            Assert.AreEqual(expectedAuthorization, request.Headers.Authorization?.ToString());
        }

        if (provider == Provider.OpenAI)
        {
            foreach (HttpRequestMessage request in requests)
                Assert.AreEqual("https://api.openai.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
        }
    }

    private static Configuration DeserializeConfiguration(string yaml)
    {
        Configuration configuration = new DeserializerBuilder().Build().Deserialize<Configuration>(yaml);
        ConfigurationValidator.Validate(configuration);
        return configuration;
    }

    private static string CreateYaml(string provider = "") =>
        $"ContentRootPath: /tmp\nPort: 80\nOpenAI:\n{(provider.Length > 0 ? "  " + provider : "")}  OpenAIEndpoint: https://api.openai.com\n  OpenAIKey: test-key\nKms:\n  DekFilePath: dek.bin\n";

    private static MemoryStream JsonBody(string json) => new(Encoding.UTF8.GetBytes(json));

    private static async Task DisposeResponseAsync(HttpResponseMessage response) => response.Dispose();

    private sealed class CaptureRequestHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(createResponse(request));
    }
}
