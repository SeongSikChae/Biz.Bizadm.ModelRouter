using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Conversion;

namespace Biz.Bizadm.ModelRouter.Tests;

[TestClass]
public sealed class StreamConversionTests
{
    private static MemoryStream Sse(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string AnthropicEvent(string type, string json)
        => $"event: {type}\ndata: {json}\n\n";

    private static async Task<List<SseEvent>> ToOpenAiAsync(string anthropicSse, bool includeUsage = false, AnthropicToolIdMapper? ids = null)
    {
        using MemoryStream output = new();
        await AnthropicToOpenAiStream.ConvertAsync(Sse(anthropicSse), output, "fallback", includeUsage, ids ?? new AnthropicToolIdMapper(), CancellationToken.None);
        output.Position = 0;
        return await ReadAllAsync(output);
    }

    private static async Task<List<SseEvent>> ToAnthropicAsync(string openAiSse, AnthropicToolIdMapper? ids = null)
    {
        using MemoryStream output = new();
        await OpenAiToAnthropicStream.ConvertAsync(Sse(openAiSse), output, "fallback", ids ?? new AnthropicToolIdMapper(), CancellationToken.None);
        output.Position = 0;
        return await ReadAllAsync(output);
    }

    private static async Task<List<SseEvent>> ReadAllAsync(Stream stream)
    {
        List<SseEvent> events = new();
        await foreach (SseEvent e in SseReader.ReadAsync(stream, CancellationToken.None))
            events.Add(e);
        return events;
    }

    private static JsonObject Data(SseEvent e) => JsonNode.Parse(e.Data)!.AsObject();

    private static string OpenAiChunk(string deltaJson, string? finish = null, string model = "gpt-x")
        => "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"model\":\"" + model + "\",\"choices\":[{\"index\":0,\"delta\":" + deltaJson
           + ",\"finish_reason\":" + (finish is null ? "null" : "\"" + finish + "\"") + "}]}\n\n";

    // ---------- SseReader ----------

    [TestMethod]
    public async Task SseReader_HandlesCrLfMultilineDataCommentsAndTrailingEvent()
    {
        List<SseEvent> events = await ReadAllAsync(Sse(": comment\r\nevent: a\r\ndata: 1\r\ndata: 2\r\n\r\ndata:3\n\ndata: tail"));

        Assert.AreEqual(3, events.Count);
        Assert.AreEqual("a", events[0].Event);
        Assert.AreEqual("1\n2", events[0].Data);
        Assert.IsNull(events[1].Event);
        Assert.AreEqual("3", events[1].Data);
        Assert.AreEqual("tail", events[2].Data);
    }

    // ---------- Anthropic → OpenAI ----------

    private const string AnthropicTextAndTools =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-x\",\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":4,\"output_tokens\":1}}}\n\n"
        + "event: ping\ndata: {\"type\":\"ping\"}\n\n"
        + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hmm\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig\"}}\n\n"
        + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
        + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n"
        + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n"
        + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_a\",\"name\":\"f\",\"input\":{}}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"a\\\":\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"1}\"}}\n\n"
        + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":2}\n\n"
        + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":3,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_b\",\"name\":\"noargs\",\"input\":{}}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":3,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\"}}\n\n"
        + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":3}\n\n"
        + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":30}}\n\n"
        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    [TestMethod]
    public async Task ToOpenAi_TextToolsThinkingDroppedAndNoArgsToolGetsEmptyObject()
    {
        List<SseEvent> events = await ToOpenAiAsync(AnthropicTextAndTools, includeUsage: true);

        Assert.AreEqual("[DONE]", events[^1].Data);
        List<JsonObject> chunks = events.Take(events.Count - 1).Select(Data).ToList();
        Assert.IsTrue(chunks.All(c => c["model"]!.GetValue<string>() == "claude-x"));

        string text = string.Concat(chunks.Select(c => c["choices"]!.AsArray().FirstOrDefault()?["delta"]?["content"]?.GetValue<string>() ?? ""));
        Assert.AreEqual("Hello", text, "thinking 내용은 버린다");

        Dictionary<int, StringBuilder> args = new();
        List<string> idsSeen = new();
        foreach (JsonObject chunk in chunks)
        {
            if (chunk["choices"]!.AsArray().FirstOrDefault()?["delta"]?["tool_calls"] is not JsonArray calls)
                continue;
            foreach (JsonNode? call in calls)
            {
                int index = call!["index"]!.GetValue<int>();
                if (call["id"] is JsonNode id)
                    idsSeen.Add(id.GetValue<string>());
                args.TryAdd(index, new StringBuilder());
                args[index].Append(call["function"]?["arguments"]?.GetValue<string>());
            }
        }

        CollectionAssert.AreEqual(new[] { "toolu_a", "toolu_b" }, idsSeen);
        Assert.AreEqual("{\"a\":1}", args[0].ToString());
        Assert.AreEqual("{}", args[1].ToString(), "빈 partial_json만 온 tool은 {}로 보정");

        JsonObject finish = chunks.Last(c => c["choices"]!.AsArray().Count > 0);
        Assert.AreEqual("tool_calls", finish["choices"]![0]!["finish_reason"]!.GetValue<string>());

        JsonObject usage = chunks[^1]["usage"]!.AsObject();
        Assert.AreEqual(0, chunks[^1]["choices"]!.AsArray().Count);
        Assert.AreEqual(14, usage["prompt_tokens"]!.GetValue<long>());
        Assert.AreEqual(30, usage["completion_tokens"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task ToOpenAi_NoUsageChunkWhenNotRequestedAndRestoresMappedIds()
    {
        AnthropicToolIdMapper ids = new();
        string mapped = ids.ToProvider("call.with:dots");
        List<SseEvent> events = await ToOpenAiAsync(AnthropicTextAndTools.Replace("toolu_a", mapped), includeUsage: false, ids);

        Assert.IsFalse(events.Take(events.Count - 1).Any(e => Data(e)["usage"] is not null));
        Assert.IsTrue(events.Any(e => e.Data.Contains("call.with:dots")));
    }

    [TestMethod]
    public async Task ToOpenAi_WorksWithoutEventLines()
    {
        string withoutEvents = string.Join("", AnthropicTextAndTools.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block[(block.IndexOf('\n') + 1)..] + "\n\n"));

        List<SseEvent> events = await ToOpenAiAsync(withoutEvents);

        Assert.AreEqual("[DONE]", events[^1].Data);
        Assert.IsFalse(events.Any(e => e.Data.Contains("\"error\"")));
    }

    [TestMethod]
    public async Task ToOpenAi_ErrorEventBecomesErrorChunkAndDone()
    {
        List<SseEvent> events = await ToOpenAiAsync(
            AnthropicEvent("message_start", "{\"type\":\"message_start\",\"message\":{\"model\":\"m\"}}")
            + AnthropicEvent("error", "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"busy\"}}"));

        Assert.AreEqual("[DONE]", events[^1].Data);
        JsonObject error = Data(events[^2]);
        Assert.AreEqual("busy", error["error"]!["message"]!.GetValue<string>());
        Assert.AreEqual("overloaded_error", error["error"]!["type"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"m\"}}\n\n")]
    [DataRow("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"m\"}}\n\ndata: not-json\n\n")]
    public async Task ToOpenAi_AbnormalEndBecomesErrorChunk(string sse)
    {
        List<SseEvent> events = await ToOpenAiAsync(sse);

        Assert.AreEqual("[DONE]", events[^1].Data);
        StringAssert.Contains(Data(events[^2])["error"]!["message"]!.GetValue<string>(), "stream ended before a terminal event");
    }

    // ---------- OpenAI → Anthropic ----------

    [TestMethod]
    public async Task ToAnthropic_AzureFirstChunkTextInterleavedToolsAndUsage()
    {
        string sse =
            "data: {\"id\":\"\",\"model\":\"\",\"choices\":[],\"prompt_filter_results\":[]}\n\n"
            + OpenAiChunk("{\"role\":\"assistant\",\"content\":\"\"}")
            + OpenAiChunk("{\"content\":\"Hel\"}")
            + OpenAiChunk("{\"content\":\"lo\"}")
            + OpenAiChunk("{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"type\":\"function\",\"function\":{\"name\":\"f\",\"arguments\":\"{\\\"x\\\":\"}}]}")
            + OpenAiChunk("{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"type\":\"function\",\"function\":{\"name\":\"g\",\"arguments\":\"{\\\"y\\\":\"}}]}")
            + OpenAiChunk("{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"function\":{\"arguments\":\"1}\"}}]}")
            + OpenAiChunk("{\"tool_calls\":[{\"index\":1,\"id\":\"call_b\",\"function\":{\"arguments\":\"2}\"}}]}")
            + OpenAiChunk("{\"tool_calls\":[{\"index\":2,\"id\":\"call_c\",\"type\":\"function\",\"function\":{\"name\":\"noargs\",\"arguments\":\"\"}}]}")
            + OpenAiChunk("{}", "tool_calls")
            + "data: {\"id\":\"c\",\"model\":\"gpt-x\",\"choices\":[],\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":9,\"prompt_tokens_details\":{\"cached_tokens\":20}}}\n\n"
            + "data: [DONE]\n\n";

        List<SseEvent> events = await ToAnthropicAsync(sse);

        CollectionAssert.AreEqual(
            new[]
            {
                "message_start",
                "content_block_start", "content_block_delta", "content_block_delta", "content_block_stop",
                "content_block_start", "content_block_delta", "content_block_stop",
                "content_block_start", "content_block_delta", "content_block_stop",
                "content_block_start", "content_block_delta", "content_block_stop",
                "message_delta", "message_stop"
            },
            events.Select(e => e.Event).ToArray());

        Assert.AreEqual("gpt-x", Data(events[0])["message"]!["model"]!.GetValue<string>(), "Azure 첫 청크의 빈 model은 쓰지 않는다");

        JsonObject[] toolStarts = events.Where((e, i) => e.Event == "content_block_start" && i > 4).Select(Data).ToArray();
        CollectionAssert.AreEqual(new[] { "call_a", "call_b", "call_c" }, toolStarts.Select(s => s["content_block"]!["id"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, toolStarts.Select(s => s["index"]!.GetValue<int>()).ToArray());

        string[] toolArgs = events.Where(e => e.Event == "content_block_delta" && e.Data.Contains("input_json_delta"))
            .Select(e => Data(e)["delta"]!["partial_json"]!.GetValue<string>()).ToArray();
        CollectionAssert.AreEqual(new[] { "{\"x\":1}", "{\"y\":2}", "{}" }, toolArgs, "교차 전송·인자 없는 tool 모두 온전");

        JsonObject messageDelta = Data(events[^2]);
        Assert.AreEqual("tool_use", messageDelta["delta"]!["stop_reason"]!.GetValue<string>());
        Assert.AreEqual(30, messageDelta["usage"]!["input_tokens"]!.GetValue<long>());
        Assert.AreEqual(20, messageDelta["usage"]!["cache_read_input_tokens"]!.GetValue<long>());
        Assert.AreEqual(9, messageDelta["usage"]!["output_tokens"]!.GetValue<long>());
    }

    [TestMethod]
    public async Task ToAnthropic_RepeatedIdOnSameIndexMakesOneBlockAndRestoresMappedId()
    {
        AnthropicToolIdMapper ids = new();
        string mapped = ids.ToProvider("call.x");
        string sse =
            OpenAiChunk("{\"tool_calls\":[{\"index\":0,\"id\":\"" + mapped + "\",\"function\":{\"name\":\"f\",\"arguments\":\"{\"}}]}")
            + OpenAiChunk("{\"tool_calls\":[{\"index\":0,\"id\":\"" + mapped + "\",\"function\":{\"name\":\"f\",\"arguments\":\"}\"}}]}")
            + OpenAiChunk("{}", "tool_calls")
            + "data: [DONE]\n\n";

        List<SseEvent> events = await ToAnthropicAsync(sse, ids);

        SseEvent toolStart = Assert.ContainsSingle(events.Where(e => e.Event == "content_block_start"));
        Assert.AreEqual("call.x", Data(toolStart)["content_block"]!["id"]!.GetValue<string>());
        Assert.AreEqual("{}", Data(events.Single(e => e.Event == "content_block_delta"))["delta"]!["partial_json"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ToAnthropic_BridgeErrorChunkBecomesErrorEventWithoutMessageStop()
    {
        string sse = OpenAiChunk("{\"content\":\"partial\"}")
            + "data: {\"error\":{\"message\":\"Responses stream ended before a terminal event.\",\"type\":\"server_error\"}}\n\n"
            + "data: [DONE]\n\n";

        List<SseEvent> events = await ToAnthropicAsync(sse);

        Assert.AreEqual("error", events[^1].Event);
        Assert.AreEqual("api_error", Data(events[^1])["error"]!["type"]!.GetValue<string>());
        Assert.IsFalse(events.Any(e => e.Event == "message_stop"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ToAnthropic_AbnormalEndBecomesErrorEvent(bool nonJson)
    {
        string sse = OpenAiChunk("{\"content\":\"x\"}") + (nonJson ? "data: <html>\n\n" : "");

        List<SseEvent> events = await ToAnthropicAsync(sse);

        Assert.AreEqual("error", events[^1].Event);
        Assert.IsFalse(events.Any(e => e.Event == "message_stop"));
    }

    [TestMethod]
    public async Task ToAnthropic_EmptyTextDeltaDoesNotOpenBlock()
    {
        List<SseEvent> events = await ToAnthropicAsync(
            OpenAiChunk("{\"role\":\"assistant\",\"content\":\"\"}") + OpenAiChunk("{}", "stop") + "data: [DONE]\n\n");

        CollectionAssert.AreEqual(new[] { "message_start", "message_delta", "message_stop" }, events.Select(e => e.Event).ToArray());
        Assert.AreEqual("end_turn", Data(events[1])["delta"]!["stop_reason"]!.GetValue<string>());
    }
}
