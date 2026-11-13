using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Conversion;

namespace Biz.Bizadm.ModelRouter.Tests;

[TestClass]
public sealed class ResponseConversionTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [TestMethod]
    public void AnthropicToOpenAi_TextToolUseThinkingAndUsage()
    {
        AnthropicToolIdMapper ids = new();
        string mapped = ids.ToProvider("call.1");
        JsonObject chat = AnthropicToOpenAiResponse.Convert(Obj("""
            {"id":"msg_1","type":"message","model":"claude-x","stop_reason":"tool_use",
             "content":[{"type":"thinking","thinking":"..."},{"type":"text","text":"a"},{"type":"text","text":"b"},
                        {"type":"tool_use","id":"__MAPPED__","name":"f","input":{"k":"v"}}],
             "usage":{"input_tokens":10,"cache_read_input_tokens":5,"cache_creation_input_tokens":2,"output_tokens":7}}
            """.Replace("__MAPPED__", mapped)), ids, "fallback");

        Assert.AreEqual("chat.completion", chat["object"]!.GetValue<string>());
        Assert.AreEqual("claude-x", chat["model"]!.GetValue<string>());
        JsonObject choice = chat["choices"]![0]!.AsObject();
        Assert.AreEqual("tool_calls", choice["finish_reason"]!.GetValue<string>());
        Assert.AreEqual("ab", choice["message"]!["content"]!.GetValue<string>());
        Assert.AreEqual("call.1", choice["message"]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.AreEqual("{\"k\":\"v\"}", choice["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());

        JsonObject usage = chat["usage"]!.AsObject();
        Assert.AreEqual(17, usage["prompt_tokens"]!.GetValue<long>());
        Assert.AreEqual(7, usage["completion_tokens"]!.GetValue<long>());
        Assert.AreEqual(24, usage["total_tokens"]!.GetValue<long>());
        Assert.AreEqual(5, usage["prompt_tokens_details"]!["cached_tokens"]!.GetValue<long>());
    }

    [TestMethod]
    public void AnthropicToOpenAi_ToolUseOnlyHasNullContentAndFallbackModel()
    {
        JsonObject chat = AnthropicToOpenAiResponse.Convert(Obj("""
            {"content":[{"type":"tool_use","id":"toolu_1","name":"f","input":{}}],"stop_reason":"tool_use"}
            """), new AnthropicToolIdMapper(), "fallback");

        Assert.AreEqual("fallback", chat["model"]!.GetValue<string>());
        Assert.IsNull(chat["choices"]![0]!["message"]!["content"]);
        Assert.AreEqual("toolu_1", chat["choices"]![0]!["message"]!["tool_calls"]![0]!["id"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("end_turn", "stop")]
    [DataRow("stop_sequence", "stop")]
    [DataRow("pause_turn", "stop")]
    [DataRow("max_tokens", "length")]
    [DataRow("model_context_window_exceeded", "length")]
    [DataRow("tool_use", "tool_calls")]
    [DataRow("refusal", "content_filter")]
    public void StopReason_ToOpenAi(string stopReason, string expected)
    {
        Assert.AreEqual(expected, StopReasonMap.ToOpenAi(stopReason));
    }

    [TestMethod]
    [DataRow("stop", "end_turn")]
    [DataRow("length", "max_tokens")]
    [DataRow("tool_calls", "tool_use")]
    [DataRow("content_filter", "refusal")]
    [DataRow(null, "end_turn")]
    public void StopReason_ToAnthropic(string? finishReason, string expected)
    {
        Assert.AreEqual(expected, StopReasonMap.ToAnthropic(finishReason));
    }

    [TestMethod]
    public void OpenAiToAnthropic_RequiredFieldsTextToolsAndUsage()
    {
        AnthropicToolIdMapper ids = new();
        string mapped = ids.ToProvider("call.1");
        JsonObject msg = OpenAiToAnthropicResponse.Convert(Obj("""
            {"id":"chatcmpl-1","model":"gpt-x","choices":[{"index":0,"finish_reason":"tool_calls",
              "message":{"role":"assistant","content":"hi","tool_calls":[
                {"id":"__MAPPED__","type":"function","function":{"name":"f","arguments":"{\"a\":1}"}},
                {"id":"call_2","type":"function","function":{"name":"g","arguments":"broken"}}]}}],
             "usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":30}}}
            """.Replace("__MAPPED__", mapped)), ids, "fallback");

        StringAssert.StartsWith(msg["id"]!.GetValue<string>(), "msg_");
        Assert.AreEqual("message", msg["type"]!.GetValue<string>());
        Assert.AreEqual("assistant", msg["role"]!.GetValue<string>());
        Assert.AreEqual("gpt-x", msg["model"]!.GetValue<string>());
        Assert.AreEqual("tool_use", msg["stop_reason"]!.GetValue<string>());
        Assert.IsTrue(msg.ContainsKey("stop_sequence"));
        Assert.IsNull(msg["stop_sequence"]);

        JsonArray content = msg["content"]!.AsArray();
        Assert.AreEqual("hi", content[0]!["text"]!.GetValue<string>());
        Assert.AreEqual("call.1", content[1]!["id"]!.GetValue<string>());
        Assert.AreEqual(1, content[1]!["input"]!["a"]!.GetValue<int>());
        Assert.AreEqual("broken", content[2]!["input"]!["raw_arguments"]!.GetValue<string>());

        JsonObject usage = msg["usage"]!.AsObject();
        Assert.AreEqual(70, usage["input_tokens"]!.GetValue<long>());
        Assert.AreEqual(30, usage["cache_read_input_tokens"]!.GetValue<long>());
        Assert.AreEqual(20, usage["output_tokens"]!.GetValue<long>());
    }

    [TestMethod]
    public void OpenAiToAnthropic_NullContentWithToolCallsAndRefusalAndEmptyChoices()
    {
        JsonObject toolOnly = OpenAiToAnthropicResponse.Convert(Obj("""
            {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"c1","function":{"name":"f","arguments":""}}]}}]}
            """), new AnthropicToolIdMapper(), "fb");
        Assert.AreEqual(1, toolOnly["content"]!.AsArray().Count);
        Assert.AreEqual("tool_use", toolOnly["content"]![0]!["type"]!.GetValue<string>());
        Assert.AreEqual(0, toolOnly["content"]![0]!["input"]!.AsObject().Count);
        Assert.AreEqual("fb", toolOnly["model"]!.GetValue<string>());

        JsonObject refusal = OpenAiToAnthropicResponse.Convert(Obj("""
            {"model":"","choices":[{"finish_reason":"stop","message":{"content":null,"refusal":"no"}}]}
            """), new AnthropicToolIdMapper(), "fb");
        Assert.AreEqual("refusal", refusal["stop_reason"]!.GetValue<string>());
        Assert.AreEqual("no", refusal["content"]![0]!["text"]!.GetValue<string>());
        Assert.AreEqual("fb", refusal["model"]!.GetValue<string>());

        JsonObject empty = OpenAiToAnthropicResponse.Convert(Obj("""{"choices":[]}"""), new AnthropicToolIdMapper(), "fb");
        Assert.AreEqual(0, empty["content"]!.AsArray().Count);
        Assert.AreEqual("end_turn", empty["stop_reason"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow(400, "invalid_request_error")]
    [DataRow(401, "authentication_error")]
    [DataRow(403, "permission_error")]
    [DataRow(404, "not_found_error")]
    [DataRow(408, "invalid_request_error")]
    [DataRow(413, "request_too_large")]
    [DataRow(429, "rate_limit_error")]
    [DataRow(500, "api_error")]
    [DataRow(529, "overloaded_error")]
    public void Error_OpenAiToAnthropicUsesStatusCode(int status, string expectedType)
    {
        JsonObject body = Obj(Encoding.UTF8.GetString(ErrorConverter.OpenAiToAnthropic(
            Encoding.UTF8.GetBytes("""{"error":{"message":"m","type":"whatever","code":"x"}}"""), status)));

        Assert.AreEqual("error", body["type"]!.GetValue<string>());
        Assert.AreEqual(expectedType, body["error"]!["type"]!.GetValue<string>());
        Assert.AreEqual("m", body["error"]!["message"]!.GetValue<string>());
    }

    [TestMethod]
    public void Error_AnthropicToOpenAiAndUnparsableBodies()
    {
        JsonObject openAi = Obj(Encoding.UTF8.GetString(ErrorConverter.AnthropicToOpenAi(
            Encoding.UTF8.GetBytes("""{"type":"error","error":{"type":"overloaded_error","message":"busy"}}"""))));
        Assert.AreEqual("busy", openAi["error"]!["message"]!.GetValue<string>());
        Assert.AreEqual("overloaded_error", openAi["error"]!["type"]!.GetValue<string>());
        Assert.IsNull(openAi["error"]!["code"]);

        JsonObject raw = Obj(Encoding.UTF8.GetString(ErrorConverter.AnthropicToOpenAi(Encoding.UTF8.GetBytes("<html>bad gateway</html>"))));
        Assert.AreEqual("<html>bad gateway</html>", raw["error"]!["message"]!.GetValue<string>());

        JsonObject rawAnthropic = Obj(Encoding.UTF8.GetString(ErrorConverter.OpenAiToAnthropic(Encoding.UTF8.GetBytes("oops"), 502)));
        Assert.AreEqual("oops", rawAnthropic["error"]!["message"]!.GetValue<string>());
        Assert.AreEqual("api_error", rawAnthropic["error"]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("rate_limit_exceeded", "rate_limit_error")]
    [DataRow("server_overloaded", "overloaded_error")]
    [DataRow("invalid_request_error", "invalid_request_error")]
    [DataRow("server_error", "api_error")]
    [DataRow("incomplete_response", "api_error")]
    public void Error_StreamChunkTypeFromCodeOrType(string typeOrCode, string expected)
    {
        string evt = ErrorConverter.OpenAiToAnthropicStreamEvent(Obj("""{"error":{"message":"m","type":"__T__"}}""".Replace("__T__", typeOrCode)));

        StringAssert.StartsWith(evt, "event: error\ndata: ");
        JsonObject data = Obj(evt["event: error\ndata: ".Length..].Trim());
        Assert.AreEqual(expected, data["error"]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    public void Error_AnthropicStreamEventToOpenAiChunk()
    {
        string chunk = ErrorConverter.AnthropicToOpenAiStreamChunk(Obj("""{"type":"error","error":{"type":"overloaded_error","message":"busy"}}"""));

        StringAssert.StartsWith(chunk, "data: ");
        JsonObject data = Obj(chunk["data: ".Length..].Trim());
        Assert.AreEqual("busy", data["error"]!["message"]!.GetValue<string>());
        Assert.AreEqual("overloaded_error", data["error"]!["type"]!.GetValue<string>());
    }
}
