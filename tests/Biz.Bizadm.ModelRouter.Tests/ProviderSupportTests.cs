using System.Configuration;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server;
using Biz.Bizadm.ModelRouter.Server.Models.Provider;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using ProviderConfiguration = Biz.Bizadm.ModelRouter.Server.Configuration.ProviderConfiguration;
using ProviderModelConfiguration = Biz.Bizadm.ModelRouter.Server.Configuration.ProviderModelConfiguration;

namespace Biz.Bizadm.ModelRouter.Tests;

[TestClass]
public sealed class ProviderSupportTests
{
    private const string ValidProviders = """
        Providers:
          foundry:
            Type: AzureFoundry
            Endpoint: https://res.services.ai.azure.com
            Key: k1
            Models:
              - Name: foundry-claude
                UpstreamModel: claude-sonnet-5-5
                Api: Anthropic
              - Name: gpt-6-luna
          anthropic:
            Type: Anthropic
            Endpoint: https://api.anthropic.com
            Key: k2
            Models:
              - Name: claude-sonnet-5-5
        """;

    [TestMethod]
    public void DeserializeConfiguration_ParsesProvidersAndDefaults()
    {
        Configuration configuration = DeserializeConfiguration(CreateYaml(ValidProviders));

        ProviderConfiguration foundry = configuration.Providers["foundry"];
        Assert.AreEqual(ProviderType.AzureFoundry, foundry.Type);
        Assert.AreEqual(ApiProtocol.Anthropic, foundry.ResolveApi(foundry.Models![0]));
        Assert.AreEqual(ApiProtocol.OpenAI, foundry.ResolveApi(foundry.Models[1]));
        Assert.AreEqual("claude-sonnet-5-5", foundry.Models[0].ResolveUpstreamModel());
        Assert.AreEqual("gpt-6-luna", foundry.Models[1].ResolveUpstreamModel());

        ProviderConfiguration anthropic = configuration.Providers["anthropic"];
        Assert.AreEqual(ApiProtocol.Anthropic, anthropic.ResolveApi(anthropic.Models![0]));
    }

    [TestMethod]
    public void DeserializeConfiguration_AllowsSameUpstreamModelAcrossProviders()
    {
        // foundry-claude(UpstreamModel=claude-sonnet-5-5)와 anthropic의 claude-sonnet-5-5가 공존한다.
        Configuration configuration = DeserializeConfiguration(CreateYaml(ValidProviders));

        Assert.AreEqual(2, configuration.Providers.Count);
    }

    [TestMethod]
    public void DeserializeConfiguration_RejectsLegacyOpenAISection()
    {
        string yaml = CreateYaml(ValidProviders) + "OpenAI:\n  OpenAIEndpoint: https://api.openai.com\n  OpenAIKey: k\n";

        Assert.ThrowsExactly<YamlException>(() => DeserializeConfiguration(yaml));
    }

    [TestMethod]
    public void DeserializeConfiguration_RejectsRemovedReasoningEffortByModel()
    {
        string yaml = CreateYaml(ValidProviders) + "ReasoningEffortByModel:\n  foundry-claude: medium\n";

        Assert.ThrowsExactly<YamlException>(() => DeserializeConfiguration(yaml));
    }

    [TestMethod]
    public void DeserializeConfiguration_RejectsUnknownType()
    {
        Assert.ThrowsExactly<YamlException>(() => DeserializeConfiguration(CreateYaml(SingleProvider("Type: Unknown"))));
    }

    [TestMethod]
    public void DeserializeConfiguration_RejectsUnknownApi()
    {
        Assert.ThrowsExactly<YamlException>(() => DeserializeConfiguration(
            CreateYaml(SingleProvider("Type: AzureFoundry", "        Api: Unknown\n"))));
    }

    [TestMethod]
    public void Validate_RejectsMissingProviders()
    {
        Assert.ThrowsExactly<Exception>(() => DeserializeConfiguration(CreateYaml("Providers: {}\n")));
    }

    [TestMethod]
    public void Validate_RejectsMissingType()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DeserializeConfiguration(CreateYaml(SingleProvider(typeLine: null))));
    }

    [TestMethod]
    public void Validate_RejectsUndefinedTypeValue()
    {
        ProviderConfiguration provider = Provider((ProviderType)99, "https://x.example", Model("m"));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(provider.Validate);
    }

    [TestMethod]
    [DataRow("relative/path")]
    [DataRow("ftp://x.example")]
    public void Validate_RejectsInvalidEndpoint(string endpoint)
    {
        ProviderConfiguration provider = Provider(ProviderType.OpenAI, endpoint, Model("m"));

        Assert.ThrowsExactly<Exception>(provider.Validate);
    }

    [TestMethod]
    public void Validate_RejectsMissingOrEmptyModels()
    {
        Assert.ThrowsExactly<Exception>(Provider(ProviderType.OpenAI, "https://x.example").Validate);
        Assert.ThrowsExactly<Exception>(() => DeserializeConfiguration(CreateYaml(SingleProvider("Type: OpenAI", modelsOverride: "    Models: []\n"))));
    }

    [TestMethod]
    public void Validate_RejectsEmptyModelName()
    {
        Assert.ThrowsExactly<Exception>(Provider(ProviderType.OpenAI, "https://x.example", Model(" ")).Validate);
    }

    [TestMethod]
    [DataRow("m-high")]
    [DataRow("m-none")]
    public void Validate_RejectsModelNameWithEffortSuffix(string name)
    {
        Assert.ThrowsExactly<Exception>(Provider(ProviderType.OpenAI, "https://x.example", Model(name)).Validate);
    }

    [TestMethod]
    public void Validate_RejectsDuplicateModelNamesAcrossProvidersIgnoringCase()
    {
        string yaml = CreateYaml("""
            Providers:
              a:
                Type: OpenAI
                Endpoint: https://api.openai.com
                Key: k
                Models:
                  - Name: Shared
              b:
                Type: Anthropic
                Endpoint: https://api.anthropic.com
                Key: k
                Models:
                  - Name: shared
            """);

        Assert.ThrowsExactly<Exception>(() => DeserializeConfiguration(yaml));
    }

    [TestMethod]
    [DataRow(ProviderType.OpenAI, ApiProtocol.Anthropic)]
    [DataRow(ProviderType.Anthropic, ApiProtocol.OpenAI)]
    public void Validate_RejectsApiNotSupportedByType(ProviderType type, ApiProtocol api)
    {
        ProviderConfiguration provider = Provider(type, "https://x.example", new ProviderModelConfiguration { Name = "m", Api = api });

        Assert.ThrowsExactly<Exception>(provider.Validate);
    }

    [TestMethod]
    [DataRow("https://res.services.ai.azure.com")]
    [DataRow("https://res.services.ai.azure.com/")]
    [DataRow("https://res.services.ai.azure.com/openai")]
    [DataRow("https://res.services.ai.azure.com/openai/v1")]
    [DataRow("https://res.services.ai.azure.com/anthropic")]
    [DataRow("https://res.services.ai.azure.com/anthropic/v1/messages")]
    public void ProviderEndpoints_NormalizeFoundryRoot(string endpoint)
    {
        ProviderConfiguration provider = Provider(ProviderType.AzureFoundry, endpoint, Model("m"));

        Assert.AreEqual("https://res.services.ai.azure.com/openai", ProviderEndpoints.OpenAiBase(provider));
        Assert.AreEqual("https://res.services.ai.azure.com/anthropic", ProviderEndpoints.AnthropicBase(provider));
    }

    [TestMethod]
    public void ProviderEndpoints_KeepNonFoundryBase()
    {
        Assert.AreEqual("https://api.openai.com", ProviderEndpoints.OpenAiBase(Provider(ProviderType.OpenAI, "https://api.openai.com/v1", Model("m"))));
        Assert.AreEqual("https://api.anthropic.com", ProviderEndpoints.AnthropicBase(Provider(ProviderType.Anthropic, "https://api.anthropic.com/", Model("m"))));
    }

    [TestMethod]
    [DataRow(ProviderType.AzureFoundry, "https://foundry.example/openai", "test-key", null)]
    [DataRow(ProviderType.OpenAI, "https://api.openai.com/v1", null, "Bearer test-key")]
    public async Task ProviderAuthentication_IsUsedByAllForwardingPaths(
        ProviderType type,
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
        AzureFoundryChatClient client = new(
            httpClient,
            Provider(type, endpoint, Model("m")),
            configuration,
            NullLogger<AzureFoundryChatClient>.Instance);

        await DisposeResponseAsync(await client.ForwardChatCompletionsAsync(
            JsonBody("{\"model\":\"m\",\"messages\":[]}"), "application/json", CancellationToken.None));
        await DisposeResponseAsync(await client.ForwardResponsesAsync(
            JsonBody("{\"model\":\"m\",\"input\":\"hello\"}"), "application/json", CancellationToken.None));
        await DisposeResponseAsync(await client.ForwardChatCompletionsAsync(
            JsonBody("{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"f\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}]}"),
            "application/json",
            CancellationToken.None));

        Assert.AreEqual(3, requests.Count);
        CollectionAssert.AreEqual(
            new[] { "chat/completions", "responses", "responses" },
            requests.Select(request => request.RequestUri!.AbsolutePath[(request.RequestUri.AbsolutePath.LastIndexOf("/v1/", StringComparison.Ordinal) + 4)..]).ToArray());

        foreach (HttpRequestMessage request in requests)
        {
            Assert.AreEqual(expectedApiKey, request.Headers.TryGetValues("api-key", out IEnumerable<string>? apiKey)
                ? Assert.ContainsSingle(apiKey)
                : null);
            Assert.AreEqual(expectedAuthorization, request.Headers.Authorization?.ToString());
        }

        if (type == ProviderType.OpenAI)
        {
            foreach (HttpRequestMessage request in requests)
                Assert.AreEqual("https://api.openai.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
        }
    }

    [TestMethod]
    public async Task UpstreamModel_RewritesModelAndKeepsPolicyByName()
    {
        List<string> bodies = new();
        using HttpClient httpClient = new(new CaptureRequestHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }));
        Configuration configuration = new()
        {
            ReasoningByModel = new Dictionary<string, ModelReasoningOptions>
            {
                ["a"] = new() { Effort = "low", ChatToolsMode = "PassThrough", SupportsNone = true }
            }
        };
        AzureFoundryChatClient client = new(
            httpClient,
            Provider(ProviderType.OpenAI, "https://api.openai.com", Model("a")),
            configuration,
            NullLogger<AzureFoundryChatClient>.Instance);

        await DisposeResponseAsync(await client.ForwardChatCompletionsAsync(
            JsonBody("{\"model\":\"a\",\"messages\":[]}"), "application/json", CancellationToken.None, upstreamModel: "b"));
        await DisposeResponseAsync(await client.ForwardChatCompletionsAsync(
            JsonBody("{\"model\":\"a-high\",\"messages\":[]}"), "application/json", CancellationToken.None, upstreamModel: "b"));

        JsonObject first = JsonNode.Parse(bodies[0])!.AsObject();
        Assert.AreEqual("b", first["model"]!.GetValue<string>());
        Assert.AreEqual("low", first["reasoning_effort"]!.GetValue<string>());

        JsonObject second = JsonNode.Parse(bodies[1])!.AsObject();
        Assert.AreEqual("b", second["model"]!.GetValue<string>());
        Assert.AreEqual("high", second["reasoning_effort"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task UpstreamModel_PreservesBytesWhenNotAliased_AndUsesNameCasingOtherwise()
    {
        List<string> bodies = new();
        using HttpClient httpClient = new(new CaptureRequestHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }));
        AzureFoundryChatClient client = new(
            httpClient,
            Provider(ProviderType.OpenAI, "https://api.openai.com", Model("Model-A")),
            new Configuration(),
            NullLogger<AzureFoundryChatClient>.Instance);

        const string exact = "{\"model\":\"Model-A\",  \"messages\":[]}";
        await DisposeResponseAsync(await client.ForwardChatCompletionsAsync(
            JsonBody(exact), "application/json", CancellationToken.None, upstreamModel: "Model-A"));
        await DisposeResponseAsync(await client.ForwardChatCompletionsAsync(
            JsonBody("{\"model\":\"model-a\",\"messages\":[]}"), "application/json", CancellationToken.None, upstreamModel: "Model-A"));

        Assert.AreEqual(exact, bodies[0]);
        Assert.AreEqual("Model-A", JsonNode.Parse(bodies[1])!["model"]!.GetValue<string>());
    }

    [TestMethod]
    public void SampleConfiguration_DeserializesAndValidates()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Biz.Bizadm.ModelRouter.slnx")))
            directory = directory.Parent;
        Assert.IsNotNull(directory, "저장소 루트를 찾지 못했습니다.");

        string yaml = File.ReadAllText(Path.Combine(directory.FullName, "src", "Biz.Bizadm.ModelRouter.Server", "Configuration.sample.yml"));
        Configuration configuration = DeserializeConfiguration(yaml);

        ModelRouteResolver resolver = new(configuration, NullLogger<ModelRouteResolver>.Instance);
        ModelRoute? foundryClaude = resolver.Resolve("foundry-claude-sonnet-5-5");
        Assert.IsNotNull(foundryClaude);
        Assert.AreEqual(ApiProtocol.Anthropic, foundryClaude.Api);
        Assert.AreEqual("claude-sonnet-5-5", foundryClaude.UpstreamModel);
        Assert.AreEqual("anthropic", resolver.Resolve("claude-sonnet-5-5")!.ProviderName);
        foreach (string key in configuration.ReasoningByModel!.Keys)
            Assert.IsNotNull(resolver.FindExact(key), $"ReasoningByModel 키 '{key}'가 Models에 없습니다.");
    }

    internal static Configuration DeserializeConfiguration(string yaml)
    {
        Configuration configuration = new DeserializerBuilder().Build().Deserialize<Configuration>(yaml);
        ConfigurationValidator.Validate(configuration);
        return configuration;
    }

    internal static string CreateYaml(string providers) =>
        "ContentRootPath: /tmp\nPort: 80\n" + providers.TrimEnd('\n') + "\nKms:\n  DekFilePath: dek.bin\n";

    private static string SingleProvider(string? typeLine, string modelExtra = "", string? modelsOverride = null)
    {
        StringBuilder builder = new();
        builder.Append("Providers:\n  p:\n");
        if (typeLine is not null)
            builder.Append("    ").Append(typeLine).Append('\n');
        builder.Append("    Endpoint: https://api.example\n    Key: k\n");
        builder.Append(modelsOverride ?? "    Models:\n      - Name: m\n" + modelExtra);
        return builder.ToString();
    }

    internal static ProviderConfiguration Provider(ProviderType type, string endpoint, params ProviderModelConfiguration[] models)
        => new() { Type = type, Endpoint = endpoint, Key = "test-key", Models = models.Length == 0 ? null : models.ToList() };

    internal static ProviderModelConfiguration Model(string name, string? upstreamModel = null, ApiProtocol? api = null)
        => new() { Name = name, UpstreamModel = upstreamModel, Api = api };

    private static MemoryStream JsonBody(string json) => new(Encoding.UTF8.GetBytes(json));

    private static Task DisposeResponseAsync(HttpResponseMessage response)
    {
        response.Dispose();
        return Task.CompletedTask;
    }

    internal sealed class CaptureRequestHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(createResponse(request));
    }
}
