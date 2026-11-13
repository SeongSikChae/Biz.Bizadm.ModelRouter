using System.Text;
using System.Text.Json.Nodes;
using static Biz.Bizadm.ModelRouter.Server.Models.Conversion.JsonNodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>Anthropic messages 비스트리밍 응답 → chat.completion (설계 4.3).</summary>
    public static class AnthropicToOpenAiResponse
    {
        public static JsonObject Convert(JsonObject msg, AnthropicToolIdMapper ids, string fallbackModel)
        {
            StringBuilder text = new StringBuilder();
            JsonArray toolCalls = new JsonArray();

            if (msg["content"] is JsonArray content)
            {
                foreach (JsonNode? node in content)
                {
                    if (node is not JsonObject block)
                        continue;

                    switch (StringOrNull(block, "type"))
                    {
                        case "text":
                            text.Append(StringOrNull(block, "text"));
                            break;
                        case "tool_use":
                            toolCalls.Add(new JsonObject
                            {
                                ["id"] = ids.Restore(StringOrNull(block, "id") ?? string.Empty),
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = StringOrNull(block, "name"),
                                    ["arguments"] = (block["input"] ?? new JsonObject()).ToJsonString()
                                }
                            });
                            break;
                    }
                }
            }

            JsonObject message = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = text.Length > 0 ? text.ToString() : null
            };
            if (toolCalls.Count > 0)
                message["tool_calls"] = toolCalls;

            return new JsonObject
            {
                ["id"] = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = NonEmpty(StringOrNull(msg, "model")) ?? fallbackModel,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = message,
                        ["finish_reason"] = StopReasonMap.ToOpenAi(StringOrNull(msg, "stop_reason"))
                    }
                },
                ["usage"] = UsageMap.ToOpenAi(msg["usage"])
            };
        }

        internal static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
