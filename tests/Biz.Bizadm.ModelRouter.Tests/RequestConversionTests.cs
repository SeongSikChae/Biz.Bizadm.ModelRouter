using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server;
using Biz.Bizadm.ModelRouter.Server.Models.Conversion;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Biz.Bizadm.ModelRouter.Tests;

[TestClass]
public sealed class RequestConversionTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject ToAnthropic(string chatJson, AnthropicToolIdMapper? ids = null)
        => OpenAiToAnthropicRequest.Convert(Obj(chatJson), ids ?? new AnthropicToolIdMapper());

    private static JsonObject ToOpenAi(string msgJson) => AnthropicToOpenAiRequest.Convert(Obj(msgJson));

    // ---------- OpenAI → Anthropic ----------

    [TestMethod]
    public void ToAnthropic_SystemTextImagesAndDefaults()
    {
        JsonObject result = ToAnthropic("""
            {"model":"m","temperature":0.2,"top_p":0.9,"n":1,"seed":3,"user":"u1","stop":"END",
             "messages":[
               {"role":"system","content":"S1"},
               {"role":"developer","content":[{"type":"text","text":"S2"}]},
               {"role":"user","content":[{"type":"text","text":"hi"},
                 {"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}},
                 {"type":"image_url","image_url":{"url":"https://x/y.png"}}]}]}
            """);

        Assert.AreEqual("S1\n\nS2", result["system"]!.GetValue<string>());
        Assert.AreEqual(16000, result["max_tokens"]!.GetValue<int>());
        Assert.AreEqual("END", result["stop_sequences"]![0]!.GetValue<string>());
        Assert.AreEqual("u1", result["metadata"]!["user_id"]!.GetValue<string>());
        foreach (string dropped in new[] { "temperature", "top_p", "n", "seed", "user", "stop" })
            Assert.IsNull(result[dropped], dropped);

        JsonArray content = result["messages"]![0]!["content"]!.AsArray();
        Assert.AreEqual("text", content[0]!["type"]!.GetValue<string>());
        Assert.AreEqual("base64", content[1]!["source"]!["type"]!.GetValue<string>());
        Assert.AreEqual("image/png", content[1]!["source"]!["media_type"]!.GetValue<string>());
        Assert.AreEqual("AAAA", content[1]!["source"]!["data"]!.GetValue<string>());
        Assert.AreEqual("url", content[2]!["source"]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public void ToAnthropic_ToolLoopMergesToolResultsAndSkipsEmptyText()
    {
        JsonObject result = ToAnthropic("""
            {"model":"m","messages":[
              {"role":"user","content":"q"},
              {"role":"assistant","content":null,"tool_calls":[
                 {"id":"call_1","type":"function","function":{"name":"a","arguments":"{\"x\":1}"}},
                 {"id":"call_2","type":"function","function":{"name":"b","arguments":"not json"}}]},
              {"role":"tool","tool_call_id":"call_1","content":"r1"},
              {"role":"tool","tool_call_id":"call_2","content":[{"type":"text","text":"r2"}]},
              {"role":"user","content":"next"},
              {"role":"assistant","content":""}]}
            """);

        JsonArray messages = result["messages"]!.AsArray();
        Assert.AreEqual(3, messages.Count, "빈 assistant 메시지는 생략");

        JsonArray assistant = messages[1]!["content"]!.AsArray();
        Assert.AreEqual(2, assistant.Count, "content:null이면 text 블록 없음");
        Assert.AreEqual("tool_use", assistant[0]!["type"]!.GetValue<string>());
        Assert.AreEqual(1, assistant[0]!["input"]!["x"]!.GetValue<int>());
        Assert.AreEqual("not json", assistant[1]!["input"]!["raw_arguments"]!.GetValue<string>());

        JsonArray user = messages[2]!["content"]!.AsArray();
        CollectionAssert.AreEqual(
            new[] { "tool_result", "tool_result", "text" },
            user.Select(b => b!["type"]!.GetValue<string>()).ToArray());
        Assert.AreEqual("r2", user[1]!["content"]!.GetValue<string>());
    }

    [TestMethod]
    public void ToAnthropic_MapsDisallowedToolIdsAndKeepsAllowedOnes()
    {
        AnthropicToolIdMapper ids = new();
        JsonObject result = ToAnthropic("""
            {"model":"m","messages":[
              {"role":"assistant","tool_calls":[
                 {"id":"call.1:x","type":"function","function":{"name":"a","arguments":"{}"}},
                 {"id":"call_ok-2","type":"function","function":{"name":"b","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"call.1:x","content":"r"}]}
            """, ids);

        string mapped = result["messages"]![0]!["content"]![0]!["id"]!.GetValue<string>();
        StringAssert.Matches(mapped, new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9_-]+$"));
        Assert.AreNotEqual("call.1:x", mapped);
        Assert.AreEqual(mapped, result["messages"]![1]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
        Assert.AreEqual("call_ok-2", result["messages"]![0]!["content"]![1]!["id"]!.GetValue<string>());
        Assert.AreEqual("call.1:x", ids.Restore(mapped));
        Assert.AreEqual(1, ids.Count);
    }

    [TestMethod]
    public void ToolIdMapper_MapsIdWithTrailingNewline()
    {
        AnthropicToolIdMapper ids = new();

        string mapped = ids.ToProvider("abc\n");

        Assert.AreNotEqual("abc\n", mapped);
        Assert.AreEqual("abc\n", ids.Restore(mapped));
        Assert.AreEqual("abc", ids.ToProvider("abc"));
    }

    [TestMethod]
    public void ToOpenAi_SystemBlocksAreSeparatedByBlankLine()
    {
        JsonObject chat = ToOpenAi("""
            {"model":"m","system":[{"type":"text","text":"A."},{"type":"text","text":""},{"type":"text","text":"B"}],"messages":[]}
            """);

        Assert.AreEqual("A.\n\nB", chat["messages"]![0]!["content"]!.GetValue<string>());
    }

    [TestMethod]
    public void ToAnthropic_ToolsChoiceAndParallel()
    {
        JsonObject result = ToAnthropic("""
            {"model":"m","messages":[{"role":"user","content":"q"}],
             "tools":[{"type":"function","function":{"name":"noargs"}},
                      {"type":"function","function":{"name":"f","description":"d","parameters":{"type":"object","properties":{"a":{"type":"string"}}}}}],
             "tool_choice":{"type":"function","function":{"name":"f"}},"parallel_tool_calls":false}
            """);

        JsonArray tools = result["tools"]!.AsArray();
        Assert.AreEqual("object", tools[0]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.AreEqual("d", tools[1]!["description"]!.GetValue<string>());
        Assert.AreEqual("tool", result["tool_choice"]!["type"]!.GetValue<string>());
        Assert.AreEqual("f", result["tool_choice"]!["name"]!.GetValue<string>());
        Assert.IsTrue(result["tool_choice"]!["disable_parallel_tool_use"]!.GetValue<bool>());
    }

    [TestMethod]
    public void ToAnthropic_ParallelFalseWithoutChoiceAndNoneChoice()
    {
        JsonObject auto = ToAnthropic("""{"model":"m","messages":[],"tools":[{"type":"function","function":{"name":"f"}}],"parallel_tool_calls":false}""");
        Assert.AreEqual("auto", auto["tool_choice"]!["type"]!.GetValue<string>());
        Assert.IsTrue(auto["tool_choice"]!["disable_parallel_tool_use"]!.GetValue<bool>());

        JsonObject none = ToAnthropic("""{"model":"m","messages":[],"tools":[{"type":"function","function":{"name":"f"}}],"tool_choice":"none","parallel_tool_calls":false}""");
        Assert.AreEqual("none", none["tool_choice"]!["type"]!.GetValue<string>());
        Assert.IsNull(none["tool_choice"]!["disable_parallel_tool_use"]);

        JsonObject noTools = ToAnthropic("""{"model":"m","messages":[],"tool_choice":"required","parallel_tool_calls":false}""");
        Assert.IsNull(noTools["tool_choice"]);
        Assert.IsNull(noTools["tools"]);
    }

    [TestMethod]
    public void ToAnthropic_RejectsNonFunctionTool()
    {
        Assert.ThrowsExactly<ConversionException>(() => ToAnthropic("""{"model":"m","messages":[],"tools":[{"type":"custom","custom":{"name":"x"}}]}"""));
    }

    [TestMethod]
    [DataRow("none", "low", false)]
    [DataRow("minimal", "low", true)]
    [DataRow("high", "high", true)]
    public void ToAnthropic_ReasoningEffort(string effort, string expected, bool thinking)
    {
        JsonObject result = ToAnthropic($$"""{"model":"m","messages":[],"reasoning_effort":"{{effort}}"}""");

        Assert.AreEqual(expected, result["output_config"]!["effort"]!.GetValue<string>());
        Assert.AreEqual(thinking, result["thinking"] is not null);
    }

    [TestMethod]
    public void ToAnthropic_JsonSchemaResponseFormatAndMaxTokens()
    {
        JsonObject result = ToAnthropic("""
            {"model":"m","messages":[],"max_completion_tokens":123,
             "response_format":{"type":"json_schema","json_schema":{"name":"x","schema":{"type":"object"}}},"reasoning_effort":"low"}
            """);

        Assert.AreEqual(123, result["max_tokens"]!.GetValue<int>());
        Assert.AreEqual("json_schema", result["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.AreEqual("object", result["output_config"]!["format"]!["schema"]!["type"]!.GetValue<string>());
        Assert.AreEqual("low", result["output_config"]!["effort"]!.GetValue<string>());

        Assert.IsNull(ToAnthropic("""{"model":"m","messages":[],"response_format":{"type":"json_object"}}""")["output_config"]);
    }

    [TestMethod]
    [DataRow("""{"model":"m"}""")]
    [DataRow("""{"model":"m","messages":{}}""")]
    [DataRow("""{"model":"m","messages":[1]}""")]
    [DataRow("""{"model":"m","messages":[{"role":3,"content":"x"}]}""")]
    [DataRow("""{"model":"m","messages":[{"role":"user","content":5}]}""")]
    [DataRow("""{"model":"m","messages":[{"role":"wizard","content":"x"}]}""")]
    [DataRow("""{"model":"m","messages":[],"reasoning_effort":5}""")]
    public void ToAnthropic_StructureErrorsBecomeConversionException(string json)
    {
        Assert.ThrowsExactly<ConversionException>(() => ToAnthropic(json));
    }

    // ---------- Anthropic → OpenAI ----------

    [TestMethod]
    public void ToOpenAi_SystemBlocksImagesToolResultsOrder()
    {
        JsonObject chat = ToOpenAi("""
            {"model":"m","max_tokens":50,"stop_sequences":["S"],"temperature":1,"top_k":3,
             "system":[{"type":"text","text":"sys"}],
             "messages":[
               {"role":"user","content":"q"},
               {"role":"assistant","content":[{"type":"thinking","thinking":"..","signature":"s"},
                  {"type":"text","text":"call"},{"type":"tool_use","id":"toolu_1","name":"f","input":{"a":1}}]},
               {"role":"user","content":[{"type":"text","text":"after"},
                  {"type":"tool_result","tool_use_id":"toolu_1","is_error":true,"content":[{"type":"text","text":"boom"},{"type":"image","source":{"type":"url","url":"u"}}]},
                  {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AA"}}]},
               {"role":"assistant","content":[{"type":"thinking","thinking":"only"}]}]}
            """);

        JsonArray messages = chat["messages"]!.AsArray();
        CollectionAssert.AreEqual(
            new[] { "system", "user", "assistant", "tool", "user" },
            messages.Select(m => m!["role"]!.GetValue<string>()).ToArray(),
            "thinking만 남은 assistant는 생략, tool_result가 user보다 먼저");

        Assert.AreEqual("call", messages[2]!["content"]!.GetValue<string>());
        Assert.AreEqual("{\"a\":1}", messages[2]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.AreEqual("[error] boom", messages[3]!["content"]!.GetValue<string>());
        Assert.AreEqual("toolu_1", messages[3]!["tool_call_id"]!.GetValue<string>());
        Assert.AreEqual("data:image/png;base64,AA", messages[4]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());

        Assert.AreEqual(50, chat["max_completion_tokens"]!.GetValue<int>());
        Assert.AreEqual("S", chat["stop"]![0]!.GetValue<string>());
        Assert.IsNull(chat["temperature"]);
        Assert.IsNull(chat["top_k"]);
        Assert.AreEqual("m", chat["model"]!.GetValue<string>());
    }

    [TestMethod]
    public void ToOpenAi_NoMaxTokensDoesNotCreateNullProperty()
    {
        Assert.IsFalse(ToOpenAi("""{"model":"m","messages":[]}""").ContainsKey("max_completion_tokens"));
    }

    [TestMethod]
    [DataRow("""{"model":"m","output_config":{"effort":"high"}}""", "m-high")]
    [DataRow("""{"model":"m","thinking":{"type":"disabled"}}""", "m-none")]
    [DataRow("""{"model":"m-low","output_config":{"effort":"high"}}""", "m-low")]
    [DataRow("""{"model":"m","thinking":{"type":"enabled","budget_tokens":2000}}""", "m")]
    [DataRow("""{"model":"m","thinking":{"type":"adaptive"}}""", "m")]
    [DataRow("""{"model":"m","output_config":{"effort":"turbo"}}""", "m")]
    public void ToOpenAi_EffortBecomesModelSuffix(string json, string expectedModel)
    {
        JsonObject msg = Obj(json);
        msg["messages"] = new JsonArray();

        Assert.AreEqual(expectedModel, AnthropicToOpenAiRequest.Convert(msg)["model"]!.GetValue<string>());
    }

    [TestMethod]
    public void ToOpenAi_ToolsChoiceFormatUserStream()
    {
        string longUser = new string('u', 100);
        JsonObject input = Obj("""
            {"model":"m","messages":[],"stream":true,
             "tools":[{"name":"f","description":"d","input_schema":{"type":"object"}}],
             "tool_choice":{"type":"any","disable_parallel_tool_use":true},
             "output_config":{"format":{"type":"json_schema","schema":{"type":"object"}}}}
            """);
        input["metadata"] = new JsonObject { ["user_id"] = longUser };
        JsonObject chat = AnthropicToOpenAiRequest.Convert(input);

        Assert.AreEqual("f", chat["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.AreEqual("required", chat["tool_choice"]!.GetValue<string>());
        Assert.IsFalse(chat["parallel_tool_calls"]!.GetValue<bool>());
        Assert.AreEqual("object", chat["response_format"]!["json_schema"]!["schema"]!["type"]!.GetValue<string>());
        Assert.IsTrue(chat["stream_options"]!["include_usage"]!.GetValue<bool>());

        string user = chat["user"]!.GetValue<string>();
        Assert.AreEqual(64, user.Length);
        Assert.AreEqual(user, ToOpenAi($$$"""{"model":"m","messages":[],"metadata":{"user_id":"{{{longUser}}}"}}""")["user"]!.GetValue<string>());
        Assert.AreEqual("short", ToOpenAi("""{"model":"m","messages":[],"metadata":{"user_id":"short"}}""")["user"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("""{"model":"m","messages":[],"tools":[{"type":"web_search_20260209","name":"web_search"}]}""")]
    [DataRow("""{"model":"m","messages":[{"role":"user","content":[{"type":"document","source":{"type":"text","data":"x"}}]}]}""")]
    [DataRow("""{"model":"m"}""")]
    [DataRow("""{"model":"m","messages":[1]}""")]
    [DataRow("""{"model":"m","messages":[{"role":7}]}""")]
    [DataRow("""{"model":"m","messages":[],"output_config":{"effort":5}}""")]
    public void ToOpenAi_UnsupportedOrMalformedBecomesConversionException(string json)
    {
        Assert.ThrowsExactly<ConversionException>(() => ToOpenAi(json));
    }

    // ---------- D9 통합: 변환 결과가 기존 정책 경로를 거치는지 ----------

    [TestMethod]
    [DataRow("""{"output_config":{"effort":"high"}}""", "PassThrough", true, "medium", "high", "chat")]
    [DataRow("""{}""", "PassThrough", true, "medium", "medium", "chat")]
    [DataRow("""{"thinking":{"type":"disabled"}}""", "PassThrough", false, null, "low", "chat")]
    [DataRow("""{"output_config":{"effort":"xhigh"}}""", "BridgeToResponses", true, "medium", "xhigh", "responses")]
    public async Task ToOpenAi_ClientEffortOverridesPolicyThroughExistingPath(
        string extra, string mode, bool supportsNone, string? policyEffort, string expectedEffort, string expectedPath)
    {
        JsonObject msg = Obj(extra);
        msg["model"] = "a";
        msg["messages"] = Obj("""{"x":[{"role":"user","content":"q"}]}""")["x"]!.DeepClone();
        if (expectedPath == "responses")
            msg["tools"] = Obj("""{"x":[{"name":"f","input_schema":{"type":"object","properties":{}}}]}""")["x"]!.DeepClone();
        JsonObject chat = AnthropicToOpenAiRequest.Convert(msg);

        List<(string Path, JsonObject Body)> sent = new();
        using HttpClient http = new(new ProviderSupportTests.CaptureRequestHandler(request =>
        {
            sent.Add((request.RequestUri!.AbsolutePath, Obj(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }));
        Configuration configuration = new()
        {
            ReasoningByModel = new Dictionary<string, ModelReasoningOptions>
            {
                ["a"] = new() { Effort = policyEffort, ChatToolsMode = mode, SupportsNone = supportsNone }
            }
        };
        AzureFoundryChatClient client = new(
            http,
            ProviderSupportTests.Provider(ProviderType.OpenAI, "https://api.openai.com", ProviderSupportTests.Model("a", "upstream-a")),
            configuration,
            NullLogger<AzureFoundryChatClient>.Instance);

        using HttpResponseMessage _ = await client.ForwardChatCompletionsAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(chat.ToJsonString())), "application/json", CancellationToken.None, upstreamModel: "upstream-a");

        (string path, JsonObject body) = Assert.ContainsSingle(sent);
        StringAssert.EndsWith(path, "/v1/" + (expectedPath == "chat" ? "chat/completions" : "responses"));
        Assert.AreEqual("upstream-a", body["model"]!.GetValue<string>());
        string actual = expectedPath == "chat"
            ? body["reasoning_effort"]!.GetValue<string>()
            : body["reasoning"]!["effort"]!.GetValue<string>();
        Assert.AreEqual(expectedEffort, actual);
    }
}
