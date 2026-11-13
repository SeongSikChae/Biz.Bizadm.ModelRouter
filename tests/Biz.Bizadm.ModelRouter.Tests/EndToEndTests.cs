using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server;
using Biz.Bizadm.ModelRouter.Server.Models.Conversion;
using Biz.Bizadm.ModelRouter.Server.Models.Provider;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Biz.Bizadm.ModelRouter.Tests;

/// <summary>TestHost로 실제 라우팅·DI·미들웨어를 거쳐 가짜 upstream으로 흐름을 검증한다(설계 D12·3.1).</summary>
[TestClass]
public sealed class EndToEndTests
{
    private sealed record Captured(HttpMethod Method, Uri Uri, HttpRequestHeaders Headers, string Body);

    private sealed class FakeUpstream : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = new();
        public Func<Captured, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Captured captured = new(request.Method, request.RequestUri!, request.Headers, body);
            Requests.Add(captured);
            return Respond(captured);
        }
    }

    private static Configuration CreateConfiguration() => new()
    {
        Providers = new Dictionary<string, Configuration.ProviderConfiguration>
        {
            ["foundry"] = ProviderSupportTests.Provider(
                ProviderType.AzureFoundry,
                "https://res.services.ai.azure.com",
                ProviderSupportTests.Model("foundry-claude", "claude-sonnet-5-5", ApiProtocol.Anthropic),
                ProviderSupportTests.Model("gpt-chat", "gpt-6-luna"),
                ProviderSupportTests.Model("gpt-bridge", "gpt-6.1-sol")),
            ["anthropic"] = ProviderSupportTests.Provider(
                ProviderType.Anthropic,
                "https://api.anthropic.com",
                ProviderSupportTests.Model("claude-sonnet-5-5"))
        },
        ReasoningByModel = new Dictionary<string, ModelReasoningOptions>
        {
            ["gpt-bridge"] = new() { Effort = "medium", ChatToolsMode = "BridgeToResponses", SupportsNone = false }
        }
    };

    private static async Task<(WebApplication App, HttpClient Client, FakeUpstream Upstream)> StartAsync()
    {
        FakeUpstream upstream = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        Program.ConfigureServices(builder.Services, CreateConfiguration());
        builder.Services.AddHttpClient(ProviderClientRegistry.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => upstream)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        WebApplication app = builder.Build();
        Program.ConfigurePipeline(app);
        await app.StartAsync();
        return (app, app.GetTestClient(), upstream);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpResponseMessage UpstreamJson(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage UpstreamSse(string sse)
        => new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };

    private static async Task<JsonObject> BodyAsync(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    private static async Task<List<SseEvent>> EventsAsync(HttpResponseMessage response)
    {
        List<SseEvent> events = new();
        await foreach (SseEvent e in SseReader.ReadAsync(await response.Content.ReadAsStreamAsync(), CancellationToken.None))
            events.Add(e);
        return events;
    }

    private static HttpRequestMessage AnthropicRequest(string path, string json)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path) { Content = Json(json) };
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "some-beta");
        return request;
    }

    // ---------- OpenAI 입력 ----------

    [TestMethod]
    public async Task ChatCompletions_OpenAiRoute_UsesFoundryOpenAiPathAndUpstreamModel()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            upstream.Respond = _ => UpstreamJson("""{"id":"c","choices":[]}""");
            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""{"model":"gpt-chat","messages":[{"role":"user","content":"hi"}]}"""));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Captured sent = Assert.ContainsSingle(upstream.Requests);
            Assert.AreEqual("https://res.services.ai.azure.com/openai/v1/chat/completions", sent.Uri.ToString());
            Assert.AreEqual("gpt-6-luna", JsonNode.Parse(sent.Body)!["model"]!.GetValue<string>());
            Assert.AreEqual("test-key", Assert.ContainsSingle(sent.Headers.GetValues("api-key")));
        }
    }

    [TestMethod]
    public async Task ChatCompletions_AnthropicRoute_NonStreamingConvertsBothWays()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            upstream.Respond = _ => UpstreamJson("""
                {"id":"msg_1","type":"message","model":"claude-sonnet-5-5","stop_reason":"end_turn",
                 "content":[{"type":"text","text":"answer"}],"usage":{"input_tokens":3,"output_tokens":4}}
                """);
            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""
                {"model":"claude-sonnet-5-5","temperature":0.3,"messages":[{"role":"system","content":"S"},{"role":"user","content":"q"}]}
                """));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Captured sent = Assert.ContainsSingle(upstream.Requests);
            Assert.AreEqual("https://api.anthropic.com/v1/messages", sent.Uri.ToString());
            JsonObject sentBody = JsonNode.Parse(sent.Body)!.AsObject();
            Assert.AreEqual("S", sentBody["system"]!.GetValue<string>());
            Assert.IsNull(sentBody["temperature"]);
            Assert.AreEqual("2023-06-01", Assert.ContainsSingle(sent.Headers.GetValues("anthropic-version")));

            JsonObject chat = await BodyAsync(response);
            Assert.AreEqual("answer", chat["choices"]![0]!["message"]!["content"]!.GetValue<string>());
            Assert.AreEqual("stop", chat["choices"]![0]!["finish_reason"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task ChatCompletions_AnthropicRoute_StreamingToolIdsAreMappedAndRestored()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            upstream.Respond = sent =>
            {
                string mapped = JsonNode.Parse(sent.Body)!["messages"]![1]!["content"]![0]!["id"]!.GetValue<string>();
                return UpstreamSse(
                    "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-sonnet-5-5\",\"usage\":{\"input_tokens\":1}}}\n\n"
                    + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"" + mapped + "\",\"name\":\"f\",\"input\":{}}}\n\n"
                    + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}}\n\n"
                    + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                    + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":2}}\n\n"
                    + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            };

            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""
                {"model":"claude-sonnet-5-5","stream":true,"stream_options":{"include_usage":true},
                 "tools":[{"type":"function","function":{"name":"f"}}],
                 "messages":[{"role":"user","content":"q"},
                   {"role":"assistant","content":null,"tool_calls":[{"id":"call.prev:1","type":"function","function":{"name":"f","arguments":"{}"}}]},
                   {"role":"tool","tool_call_id":"call.prev:1","content":"r"}]}
                """));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            JsonObject sentBody = JsonNode.Parse(Assert.ContainsSingle(upstream.Requests).Body)!.AsObject();
            string mappedId = sentBody["messages"]![1]!["content"]![0]!["id"]!.GetValue<string>();
            StringAssert.Matches(mappedId, new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9_-]+$"));
            Assert.AreEqual(mappedId, sentBody["messages"]![2]!["content"]![0]!["tool_use_id"]!.GetValue<string>());

            List<SseEvent> events = await EventsAsync(response);
            Assert.AreEqual("[DONE]", events[^1].Data);
            string toolId = events.Select(e => JsonNode.Parse(e.Data == "[DONE]" ? "{}" : e.Data)!)
                .Select(n => n["choices"]?.AsArray().FirstOrDefault()?["delta"]?["tool_calls"]?[0]?["id"]?.GetValue<string>())
                .First(id => id is not null)!;
            Assert.AreEqual("call.prev:1", toolId);
            Assert.IsTrue(events.Any(e => e.Data.Contains("\"usage\"")));
        }
    }

    [TestMethod]
    public async Task ChatCompletions_AnthropicRoute_UpstreamErrorIsConvertedAndRetryAfterCopied()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            upstream.Respond = _ =>
            {
                HttpResponseMessage error = UpstreamJson("""{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", (HttpStatusCode)429);
                error.Headers.TryAddWithoutValidation("Retry-After", "7");
                return error;
            };

            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":"q"}]}"""));

            Assert.AreEqual((HttpStatusCode)429, response.StatusCode);
            Assert.AreEqual("7", Assert.ContainsSingle(response.Headers.GetValues("Retry-After")));
            JsonObject body = await BodyAsync(response);
            Assert.AreEqual("slow down", body["error"]!["message"]!.GetValue<string>());
            Assert.AreEqual("rate_limit_error", body["error"]!["type"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task ChatCompletions_AnthropicRoute_StructureErrorIsOpenAi400()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""{"model":"claude-sonnet-5-5","messages":[{"role":3}]}"""));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("invalid_request_error", (await BodyAsync(response))["error"]!["type"]!.GetValue<string>());
            Assert.AreEqual(0, upstream.Requests.Count);
        }
    }

    [TestMethod]
    [DataRow("/v1/chat/completions", """{"model":"unknown","messages":[]}""", "model_not_found")]
    [DataRow("/v1/chat/completions", "not json", "model_not_found")]
    [DataRow("/v1/responses", """{"model":"unknown","input":"x"}""", "model_not_found")]
    public async Task OpenAiEndpoints_UnknownModelOrUnparsableBodyIs404(string path, string body, string code)
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.PostAsync(path, Json(body));

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.AreEqual(code, (await BodyAsync(response))["error"]!["code"]!.GetValue<string>());
            Assert.AreEqual(0, upstream.Requests.Count);
        }
    }

    [TestMethod]
    public async Task Responses_AnthropicRouteIs400()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.PostAsync("/v1/responses", Json("""{"model":"foundry-claude","input":"x"}"""));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual(0, upstream.Requests.Count);
        }
    }

    // ---------- Anthropic 입력 ----------

    [TestMethod]
    public async Task Messages_AnthropicRoute_PassesThroughBodyAndHeaders()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            upstream.Respond = _ =>
            {
                HttpResponseMessage ok = UpstreamJson("""{"id":"msg_1","type":"message","content":[]}""");
                ok.Headers.TryAddWithoutValidation("request-id", "req_123");
                ok.Headers.TryAddWithoutValidation("anthropic-ratelimit-requests-remaining", "99");
                return ok;
            };
            const string body = """{"model":"claude-sonnet-5-5",  "max_tokens":10,"messages":[{"role":"user","content":"q"}]}""";

            HttpResponseMessage response = await client.SendAsync(AnthropicRequest("/v1/messages", body));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Captured sent = Assert.ContainsSingle(upstream.Requests);
            Assert.AreEqual("https://api.anthropic.com/v1/messages", sent.Uri.ToString());
            Assert.AreEqual(body, sent.Body);
            Assert.AreEqual("some-beta", Assert.ContainsSingle(sent.Headers.GetValues("anthropic-beta")));
            Assert.AreEqual("req_123", Assert.ContainsSingle(response.Headers.GetValues("request-id")));
            Assert.AreEqual("99", Assert.ContainsSingle(response.Headers.GetValues("anthropic-ratelimit-requests-remaining")));
        }
    }

    [TestMethod]
    public async Task Messages_FoundryClaudeAlias_RewritesModelAndUsesAnthropicPath()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(AnthropicRequest("/v1/messages", """{"model":"foundry-claude-high","max_tokens":10,"messages":[]}"""));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Captured sent = Assert.ContainsSingle(upstream.Requests);
            Assert.AreEqual("https://res.services.ai.azure.com/anthropic/v1/messages", sent.Uri.ToString());
            JsonObject sentBody = JsonNode.Parse(sent.Body)!.AsObject();
            Assert.AreEqual("claude-sonnet-5-5", sentBody["model"]!.GetValue<string>());
            Assert.AreEqual("high", sentBody["output_config"]!["effort"]!.GetValue<string>());
            Assert.AreEqual("test-key", Assert.ContainsSingle(sent.Headers.GetValues("x-api-key")));
        }
    }

    [TestMethod]
    public async Task Messages_OpenAiRoute_NonStreamingConvertsBothWays()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            upstream.Respond = _ => UpstreamJson("""
                {"id":"c","model":"gpt-6-luna","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"hello"}}],
                 "usage":{"prompt_tokens":5,"completion_tokens":2}}
                """);

            HttpResponseMessage response = await client.SendAsync(AnthropicRequest("/v1/messages", """
                {"model":"gpt-chat","max_tokens":10,"system":"S","output_config":{"effort":"low"},"messages":[{"role":"user","content":"q"}]}
                """));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Captured sent = Assert.ContainsSingle(upstream.Requests);
            Assert.AreEqual("https://res.services.ai.azure.com/openai/v1/chat/completions", sent.Uri.ToString());
            JsonObject sentBody = JsonNode.Parse(sent.Body)!.AsObject();
            Assert.AreEqual("gpt-6-luna", sentBody["model"]!.GetValue<string>());
            Assert.AreEqual("low", sentBody["reasoning_effort"]!.GetValue<string>());
            Assert.AreEqual("system", sentBody["messages"]![0]!["role"]!.GetValue<string>());

            JsonObject message = await BodyAsync(response);
            Assert.AreEqual("message", message["type"]!.GetValue<string>());
            Assert.AreEqual("hello", message["content"]![0]!["text"]!.GetValue<string>());
            Assert.AreEqual("end_turn", message["stop_reason"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task Messages_OpenAiBridgeRoute_StreamingToolsGoThroughResponsesAndBack()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            upstream.Respond = _ => UpstreamSse(
                "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"model\":\"gpt-6.1-sol\"}}\n\n"
                + "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Let me check\"}\n\n"
                + "data: {\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_new\",\"name\":\"lookup\"}}\n\n"
                + "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":1,\"delta\":\"{\\\"q\\\":1}\"}\n\n"
                + "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"model\":\"gpt-6.1-sol\",\"status\":\"completed\",\"output\":[]}}\n\n");

            HttpResponseMessage response = await client.SendAsync(AnthropicRequest("/v1/messages", """
                {"model":"gpt-bridge","max_tokens":100,"stream":true,
                 "tools":[{"name":"lookup","input_schema":{"type":"object","properties":{"q":{"type":"integer"}}}}],
                 "messages":[{"role":"user","content":"q"},
                   {"role":"assistant","content":[{"type":"tool_use","id":"toolu_prev","name":"lookup","input":{"q":0}}]},
                   {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_prev","content":"0"}]}]}
                """));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Captured sent = Assert.ContainsSingle(upstream.Requests);
            StringAssert.EndsWith(sent.Uri.AbsolutePath, "/openai/v1/responses");
            Assert.AreEqual("gpt-6.1-sol", JsonNode.Parse(sent.Body)!["model"]!.GetValue<string>());

            List<SseEvent> events = await EventsAsync(response);
            Assert.AreEqual("message_start", events[0].Event);
            Assert.AreEqual("message_stop", events[^1].Event);
            Assert.IsFalse(events.Any(e => e.Event == "error"));
            Assert.IsTrue(events.Any(e => e.Data.Contains("Let me check")));

            JsonObject toolStart = events.Where(e => e.Event == "content_block_start").Select(e => JsonNode.Parse(e.Data)!.AsObject())
                .Single(e => e["content_block"]!["type"]!.GetValue<string>() == "tool_use");
            Assert.AreEqual("call_new", toolStart["content_block"]!["id"]!.GetValue<string>());
            Assert.AreEqual("lookup", toolStart["content_block"]!["name"]!.GetValue<string>());
            string partialJson = events.Where(e => e.Event == "content_block_delta")
                .Select(e => JsonNode.Parse(e.Data)!["delta"]!)
                .Single(d => d["type"]!.GetValue<string>() == "input_json_delta")["partial_json"]!.GetValue<string>();
            Assert.AreEqual("{\"q\":1}", partialJson);
            Assert.AreEqual("tool_use", JsonNode.Parse(events[^2].Data)!["delta"]!["stop_reason"]!.GetValue<string>());
        }
    }

    [TestMethod]
    [DataRow("""{"model":"gpt-chat","messages":[],"tools":[{"type":"web_search_20260209","name":"web_search"}]}""")]
    [DataRow("""{"model":"gpt-chat","messages":[{"role":7}]}""")]
    public async Task Messages_OpenAiRoute_UnsupportedOrMalformedIsAnthropic400(string body)
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(AnthropicRequest("/v1/messages", body));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            JsonObject error = await BodyAsync(response);
            Assert.AreEqual("error", error["type"]!.GetValue<string>());
            Assert.AreEqual("invalid_request_error", error["error"]!["type"]!.GetValue<string>());
            Assert.AreEqual(0, upstream.Requests.Count);
        }
    }

    [TestMethod]
    [DataRow("/v1/messages", """{"model":"nope","messages":[]}""")]
    [DataRow("/v1/messages", "{bad")]
    [DataRow("/v1/messages/count_tokens", """{"model":"nope","messages":[]}""")]
    public async Task AnthropicEndpoints_UnknownModelIsAnthropic404(string path, string body)
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(AnthropicRequest(path, body));

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.AreEqual("not_found_error", (await BodyAsync(response))["error"]!["type"]!.GetValue<string>());
        }
    }

    [TestMethod]
    public async Task CountTokens_AnthropicRouteForwardsAndOpenAiRouteEstimates()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            upstream.Respond = _ => UpstreamJson("""{"input_tokens":42}""");

            HttpResponseMessage forwarded = await client.SendAsync(AnthropicRequest("/v1/messages/count_tokens", """{"model":"claude-sonnet-5-5-high","messages":[]}"""));
            Assert.AreEqual(42, (await BodyAsync(forwarded))["input_tokens"]!.GetValue<int>());
            Captured sent = Assert.ContainsSingle(upstream.Requests);
            Assert.AreEqual("https://api.anthropic.com/v1/messages/count_tokens", sent.Uri.ToString());
            JsonObject sentBody = JsonNode.Parse(sent.Body)!.AsObject();
            Assert.AreEqual("claude-sonnet-5-5", sentBody["model"]!.GetValue<string>());
            Assert.IsNull(sentBody["output_config"]);

            HttpResponseMessage estimated = await client.SendAsync(AnthropicRequest("/v1/messages/count_tokens", """{"model":"gpt-chat","messages":[{"role":"user","content":"12345678"}]}"""));
            Assert.AreEqual(HttpStatusCode.OK, estimated.StatusCode);
            Assert.AreEqual(2, (await BodyAsync(estimated))["input_tokens"]!.GetValue<int>());
            Assert.AreEqual(1, upstream.Requests.Count, "근사치는 upstream을 호출하지 않는다");
        }
    }

    // ---------- 모델 목록 ----------

    [TestMethod]
    public async Task Models_ListAndGetInBothFormatsWithoutUpstreamCalls()
    {
        (WebApplication app, HttpClient client, FakeUpstream upstream) = await StartAsync();
        await using (app)
        {
            JsonObject openAi = await BodyAsync(await client.GetAsync("/v1/models"));
            Assert.AreEqual("list", openAi["object"]!.GetValue<string>());
            CollectionAssert.AreEqual(
                new[] { "foundry-claude", "gpt-chat", "gpt-bridge", "claude-sonnet-5-5" },
                openAi["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToArray());
            Assert.AreEqual("foundry", openAi["data"]![0]!["owned_by"]!.GetValue<string>());

            HttpRequestMessage anthropicList = new(HttpMethod.Get, "/v1/models");
            anthropicList.Headers.Add("anthropic-version", "2023-06-01");
            JsonObject anthropic = await BodyAsync(await client.SendAsync(anthropicList));
            Assert.AreEqual("model", anthropic["data"]![0]!["type"]!.GetValue<string>());
            Assert.IsFalse(anthropic["has_more"]!.GetValue<bool>());
            Assert.AreEqual("foundry-claude", anthropic["first_id"]!.GetValue<string>());
            Assert.AreEqual("claude-sonnet-5-5", anthropic["last_id"]!.GetValue<string>());

            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/v1/models/GPT-CHAT")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/v1/models/gpt-chat-high")).StatusCode);
            Assert.AreEqual(0, upstream.Requests.Count);
        }
    }
}
