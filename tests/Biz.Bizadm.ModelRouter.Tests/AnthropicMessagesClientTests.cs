using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Anthropic;
using Microsoft.Extensions.Logging.Abstractions;

namespace Biz.Bizadm.ModelRouter.Tests;

[TestClass]
public sealed class AnthropicMessagesClientTests
{
    private sealed record Captured(HttpRequestMessage Request, string Body);

    private static (AnthropicMessagesClient Client, List<Captured> Requests, HttpClient Http) Create(ProviderType type, string endpoint)
    {
        List<Captured> requests = new();
        HttpClient http = new(new ProviderSupportTests.CaptureRequestHandler(request =>
        {
            requests.Add(new Captured(request, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }));
        AnthropicMessagesClient client = new(
            http,
            ProviderSupportTests.Provider(type, endpoint, ProviderSupportTests.Model("m", api: type == ProviderType.AzureFoundry ? ApiProtocol.Anthropic : null)),
            NullLogger<AnthropicMessagesClient>.Instance);
        return (client, requests, http);
    }

    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    [TestMethod]
    [DataRow(ProviderType.Anthropic, "https://api.anthropic.com", "https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/messages/count_tokens")]
    [DataRow(ProviderType.AzureFoundry, "https://res.services.ai.azure.com/openai", "https://res.services.ai.azure.com/anthropic/v1/messages", "https://res.services.ai.azure.com/anthropic/v1/messages/count_tokens")]
    public async Task Forward_UsesProviderUrlAndAnthropicHeaders(ProviderType type, string endpoint, string messagesUrl, string countTokensUrl)
    {
        (AnthropicMessagesClient client, List<Captured> requests, HttpClient http) = Create(type, endpoint);
        using (http)
        {
            (await client.ForwardMessagesAsync(Json("{\"model\":\"m\"}"), null, null, null, "m", CancellationToken.None)).Dispose();
            (await client.ForwardCountTokensAsync(Json("{\"model\":\"m\"}"), null, "2024-01-01", "beta-x", "m", CancellationToken.None)).Dispose();
        }

        Assert.AreEqual(messagesUrl, requests[0].Request.RequestUri!.ToString());
        Assert.AreEqual(countTokensUrl, requests[1].Request.RequestUri!.ToString());

        Assert.AreEqual("test-key", Assert.ContainsSingle(requests[0].Request.Headers.GetValues("x-api-key")));
        Assert.AreEqual("2023-06-01", Assert.ContainsSingle(requests[0].Request.Headers.GetValues("anthropic-version")));
        Assert.IsFalse(requests[0].Request.Headers.Contains("anthropic-beta"));
        Assert.IsFalse(requests[0].Request.Headers.Contains("api-key"));
        Assert.IsNull(requests[0].Request.Headers.Authorization);

        Assert.AreEqual("2024-01-01", Assert.ContainsSingle(requests[1].Request.Headers.GetValues("anthropic-version")));
        Assert.AreEqual("beta-x", Assert.ContainsSingle(requests[1].Request.Headers.GetValues("anthropic-beta")));
    }

    [TestMethod]
    public async Task ForwardMessages_PreservesBytesWithoutAliasOrSuffix()
    {
        const string body = "{\"model\":\"m\",   \"max_tokens\":10,\"messages\":[]}";
        (AnthropicMessagesClient client, List<Captured> requests, HttpClient http) = Create(ProviderType.Anthropic, "https://api.anthropic.com");
        using (http)
            (await client.ForwardMessagesAsync(Json(body), "application/json", null, null, "m", CancellationToken.None)).Dispose();

        Assert.AreEqual(body, requests[0].Body);
    }

    [TestMethod]
    public async Task ForwardMessages_RewritesAliasToUpstreamModel()
    {
        (AnthropicMessagesClient client, List<Captured> requests, HttpClient http) = Create(ProviderType.Anthropic, "https://api.anthropic.com");
        using (http)
            (await client.ForwardMessagesAsync(Json("{\"model\":\"alias\",\"messages\":[]}"), null, null, null, "claude-sonnet-5-5", CancellationToken.None)).Dispose();

        JsonObject sent = JsonNode.Parse(requests[0].Body)!.AsObject();
        Assert.AreEqual("claude-sonnet-5-5", sent["model"]!.GetValue<string>());
        Assert.IsNull(sent["thinking"]);
        Assert.IsNull(sent["output_config"]);
    }

    [TestMethod]
    public async Task ForwardMessages_HighSuffixAppliesAdaptiveThinkingAndEffort()
    {
        (AnthropicMessagesClient client, List<Captured> requests, HttpClient http) = Create(ProviderType.Anthropic, "https://api.anthropic.com");
        using (http)
            (await client.ForwardMessagesAsync(Json("{\"model\":\"alias-high\",\"messages\":[]}"), null, null, null, "up", CancellationToken.None)).Dispose();

        JsonObject sent = JsonNode.Parse(requests[0].Body)!.AsObject();
        Assert.AreEqual("up", sent["model"]!.GetValue<string>());
        Assert.AreEqual("adaptive", sent["thinking"]!["type"]!.GetValue<string>());
        Assert.AreEqual("high", sent["output_config"]!["effort"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ForwardMessages_NoneSuffixRemovesThinkingAndUsesLow()
    {
        (AnthropicMessagesClient client, List<Captured> requests, HttpClient http) = Create(ProviderType.Anthropic, "https://api.anthropic.com");
        using (http)
            (await client.ForwardMessagesAsync(Json("{\"model\":\"m-none\",\"thinking\":{\"type\":\"adaptive\"},\"messages\":[]}"), null, null, null, "m", CancellationToken.None)).Dispose();

        JsonObject sent = JsonNode.Parse(requests[0].Body)!.AsObject();
        Assert.AreEqual("m", sent["model"]!.GetValue<string>());
        Assert.IsNull(sent["thinking"]);
        Assert.AreEqual("low", sent["output_config"]!["effort"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ForwardCountTokens_StripsSuffixWithoutAddingEffort()
    {
        (AnthropicMessagesClient client, List<Captured> requests, HttpClient http) = Create(ProviderType.Anthropic, "https://api.anthropic.com");
        using (http)
            (await client.ForwardCountTokensAsync(Json("{\"model\":\"m-high\",\"messages\":[]}"), null, null, null, "m", CancellationToken.None)).Dispose();

        JsonObject sent = JsonNode.Parse(requests[0].Body)!.AsObject();
        Assert.AreEqual("m", sent["model"]!.GetValue<string>());
        Assert.IsNull(sent["thinking"]);
        Assert.IsNull(sent["output_config"]);
    }

    [TestMethod]
    [DataRow("minimal", "low", true)]
    [DataRow("xhigh", "xhigh", true)]
    [DataRow("none", "low", false)]
    public void ApplyEffort_MapsOpenAiEffortValues(string effort, string expectedEffort, bool expectThinking)
    {
        JsonObject root = new() { ["thinking"] = new JsonObject { ["type"] = "adaptive" } };

        AnthropicReasoning.ApplyEffort(root, effort);

        Assert.AreEqual(expectedEffort, root["output_config"]!["effort"]!.GetValue<string>());
        Assert.AreEqual(expectThinking, root["thinking"] is not null);
    }

    [TestMethod]
    public void ApplyEffort_IgnoresUnknownValue()
    {
        JsonObject root = new();

        AnthropicReasoning.ApplyEffort(root, "turbo");

        Assert.IsNull(root["output_config"]);
        Assert.IsNull(root["thinking"]);
    }
}
