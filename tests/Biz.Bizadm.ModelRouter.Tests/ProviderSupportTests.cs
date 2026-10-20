using System.Configuration;
using System.Net;
using System.Text;
using Biz.Bizadm.ModelRouter.Server;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Core;

namespace Biz.Bizadm.ModelRouter.Tests;

public sealed class ProviderSupportTests
{
    [Fact]
    public void DeserializeConfiguration_ProviderDefaultsToAzureFoundry()
    {
        Configuration configuration = DeserializeConfiguration(CreateYaml());

        Assert.Equal(Provider.AzureFoundry, configuration.Provider);
    }

    [Theory]
    [InlineData("AzureFoundry", Provider.AzureFoundry)]
    [InlineData("OpenAI", Provider.OpenAI)]
    public void DeserializeConfiguration_ParsesProvider(string provider, Provider expected)
    {
        Configuration configuration = DeserializeConfiguration(CreateYaml($"Provider: {provider}\n"));

        Assert.Equal(expected, configuration.Provider);
    }

    [Fact]
    public void DeserializeConfiguration_RejectsUnknownProvider()
    {
        Assert.Throws<YamlException>(() => DeserializeConfiguration(CreateYaml("Provider: Unknown\n")));
    }

    [Fact]
    public void Validate_RejectsUndefinedProviderValue()
    {
        Configuration configuration = new() { Provider = (Provider)99 };

        Assert.Throws<ArgumentOutOfRangeException>(configuration.Validate);
    }

    [Theory]
    [InlineData(Provider.AzureFoundry, "https://foundry.example/openai", "test-key", null)]
    [InlineData(Provider.OpenAI, "https://api.openai.com/v1", null, "Bearer test-key")]
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
            Provider = provider,
            OpenAIEndpoint = endpoint,
            OpenAIKey = "test-key",
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

        Assert.Equal(5, requests.Count);
        Assert.Equal(
            new[] { "chat/completions", "responses", "models", "models/model%20name", "responses" },
            requests.Select(request => request.RequestUri!.AbsolutePath[(request.RequestUri.AbsolutePath.LastIndexOf("/v1/", StringComparison.Ordinal) + 4)..]));

        foreach (HttpRequestMessage request in requests)
        {
            Assert.Equal(expectedApiKey, request.Headers.TryGetValues("api-key", out IEnumerable<string>? apiKey)
                ? Assert.Single(apiKey)
                : null);
            Assert.Equal(expectedAuthorization, request.Headers.Authorization?.ToString());
        }

        if (provider == Provider.OpenAI)
            Assert.All(requests, request => Assert.Equal("https://api.openai.com", request.RequestUri!.GetLeftPart(UriPartial.Authority)));
    }

    private static Configuration DeserializeConfiguration(string yaml)
    {
        Configuration configuration = new DeserializerBuilder().Build().Deserialize<Configuration>(yaml);
        ConfigurationValidator.Validate(configuration);
        return configuration;
    }

    private static string CreateYaml(string provider = "") =>
        $"{provider}ContentRootPath: /tmp\nPort: 80\nOpenAIEndpoint: https://api.openai.com\nOpenAIKey: test-key\n";

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
