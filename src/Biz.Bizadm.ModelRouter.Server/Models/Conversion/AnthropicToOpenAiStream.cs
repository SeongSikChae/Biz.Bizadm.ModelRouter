using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Biz.Bizadm.ModelRouter.Server.Models.Conversion.JsonNodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>Anthropic messages SSE → chat.completion.chunk SSE (설계 4.3).</summary>
    public static class AnthropicToOpenAiStream
    {
        public const string StreamEndedMessage = "stream ended before a terminal event";

        public static async Task ConvertAsync(
            Stream upstream,
            Stream output,
            string fallbackModel,
            bool includeUsage,
            AnthropicToolIdMapper ids,
            CancellationToken cancellationToken)
        {
            string id = "chatcmpl-" + Guid.NewGuid().ToString("N");
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string model = fallbackModel;
            int toolIndex = -1;
            Dictionary<int, ToolBlock> tools = new Dictionary<int, ToolBlock>();
            JsonObject? usage = null;

            async Task WriteAsync(string text)
            {
                await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            Task WriteChunkAsync(JsonObject delta, string? finishReason)
            {
                JsonObject chunk = new JsonObject
                {
                    ["id"] = id,
                    ["object"] = "chat.completion.chunk",
                    ["created"] = created,
                    ["model"] = model,
                    ["choices"] = new JsonArray
                    {
                        new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason }
                    }
                };
                return WriteAsync($"data: {chunk.ToJsonString()}\n\n");
            }

            Task WriteToolArgumentsAsync(int index, string arguments)
                => WriteChunkAsync(new JsonObject
                {
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject { ["index"] = index, ["function"] = new JsonObject { ["arguments"] = arguments } }
                    }
                }, null);

            try
            {
                await foreach (SseEvent sse in SseReader.ReadAsync(upstream, cancellationToken))
                {
                    if (!TryParseObject(sse.Data, out JsonObject? data))
                        break;

                    switch (sse.Event ?? TypeOf(data))
                    {
                        case "message_start":
                            model = AnthropicToOpenAiResponse.NonEmpty(TypeOrNull(data["message"], "model")) ?? fallbackModel;
                            usage = data["message"]?["usage"]?.DeepClone() as JsonObject;
                            await WriteChunkAsync(new JsonObject { ["role"] = "assistant", ["content"] = "" }, null);
                            break;

                        case "content_block_start":
                            if (TypeOrNull(data["content_block"], "type") == "tool_use")
                            {
                                int blockIndex = BlockIndex(data);
                                tools[blockIndex] = new ToolBlock(++toolIndex);
                                await WriteChunkAsync(new JsonObject
                                {
                                    ["tool_calls"] = new JsonArray
                                    {
                                        new JsonObject
                                        {
                                            ["index"] = toolIndex,
                                            ["id"] = ids.Restore(TypeOrNull(data["content_block"], "id") ?? string.Empty),
                                            ["type"] = "function",
                                            ["function"] = new JsonObject
                                            {
                                                ["name"] = TypeOrNull(data["content_block"], "name"),
                                                ["arguments"] = ""
                                            }
                                        }
                                    }
                                }, null);
                            }
                            break;

                        case "content_block_delta":
                            JsonNode? delta = data["delta"];
                            switch (TypeOrNull(delta, "type"))
                            {
                                case "text_delta":
                                    if (TypeOrNull(delta, "text") is { Length: > 0 } text)
                                        await WriteChunkAsync(new JsonObject { ["content"] = text }, null);
                                    break;
                                case "input_json_delta":
                                    if (tools.TryGetValue(BlockIndex(data), out ToolBlock? tool)
                                        && TypeOrNull(delta, "partial_json") is { Length: > 0 } partial)
                                    {
                                        tool.HasArguments = true;
                                        await WriteToolArgumentsAsync(tool.Index, partial);
                                    }
                                    break;
                            }
                            break;

                        case "content_block_stop":
                            if (tools.TryGetValue(BlockIndex(data), out ToolBlock? stopped) && !stopped.HasArguments)
                            {
                                stopped.HasArguments = true;
                                await WriteToolArgumentsAsync(stopped.Index, "{}");
                            }
                            break;

                        case "message_delta":
                            usage = MergeUsage(usage, data["usage"]);
                            await WriteChunkAsync(new JsonObject(), StopReasonMap.ToOpenAi(TypeOrNull(data["delta"], "stop_reason")));
                            break;

                        case "message_stop":
                            if (includeUsage)
                            {
                                JsonObject usageChunk = new JsonObject
                                {
                                    ["id"] = id,
                                    ["object"] = "chat.completion.chunk",
                                    ["created"] = created,
                                    ["model"] = model,
                                    ["choices"] = new JsonArray(),
                                    ["usage"] = UsageMap.ToOpenAi(usage)
                                };
                                await WriteAsync($"data: {usageChunk.ToJsonString()}\n\n");
                            }
                            await WriteAsync("data: [DONE]\n\n");
                            return;

                        case "error":
                            await WriteAsync(ErrorConverter.AnthropicToOpenAiStreamChunk(data));
                            await WriteAsync("data: [DONE]\n\n");
                            return;
                    }
                }

            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // upstream 연결이 전송 중에 끊겼다. 아래 비정상 종료 경로로 알린다.
            }

            // message_stop 없이 끊겼거나 JSON이 아닌 data를 만났다.
            await WriteAsync(ErrorConverter.OpenAiErrorChunk("api_error", StreamEndedMessage));
            await WriteAsync("data: [DONE]\n\n");
        }

        internal static JsonObject MergeUsage(JsonObject? usage, JsonNode? delta)
        {
            JsonObject merged = usage ?? new JsonObject();
            if (delta is JsonObject deltaObject)
            {
                foreach ((string name, JsonNode? value) in deltaObject)
                {
                    if (value is JsonValue)
                        merged[name] = value.DeepClone();
                }
            }
            return merged;
        }

        internal static bool TryParseObject(string data, out JsonObject? root)
        {
            try
            {
                root = JsonNode.Parse(data) as JsonObject;
            }
            catch (JsonException)
            {
                root = null;
            }
            return root is not null;
        }

        private static string? TypeOf(JsonObject data) => TypeOrNull(data, "type");

        private static string? TypeOrNull(JsonNode? node, string name)
            => node?[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

        private static int BlockIndex(JsonObject data)
            => data["index"] is JsonValue value && value.TryGetValue(out int index) ? index : -1;

        private sealed class ToolBlock(int index)
        {
            public int Index { get; } = index;
            public bool HasArguments { get; set; }
        }
    }
}
