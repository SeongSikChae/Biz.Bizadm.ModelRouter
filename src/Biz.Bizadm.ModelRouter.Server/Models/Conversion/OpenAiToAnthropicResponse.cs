using System.Text.Json.Nodes;
using static Biz.Bizadm.ModelRouter.Server.Models.Conversion.JsonNodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>chat.completion 비스트리밍 응답 → Anthropic message (설계 4.3).</summary>
    public static class OpenAiToAnthropicResponse
    {
        public static JsonObject Convert(JsonObject chat, AnthropicToolIdMapper ids, string fallbackModel)
        {
            JsonObject? choice = (chat["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
            JsonObject message = choice?["message"] as JsonObject ?? new JsonObject();

            JsonArray content = new JsonArray();
            string? refusal = NonEmptyString(message["refusal"]);
            string text = refusal ?? TextOrEmpty(message["content"]);
            if (text.Length > 0)
                content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

            if (message["tool_calls"] is JsonArray toolCalls)
            {
                foreach (JsonNode? node in toolCalls)
                {
                    if (node is not JsonObject call)
                        continue;
                    JsonNode? function = call["function"];
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = ids.Restore(StringOrNull(call, "id") ?? string.Empty),
                        ["name"] = StringOrNull(function, "name"),
                        ["input"] = ParseArguments(StringOrNull(function, "arguments"))
                    });
                }
            }

            return new JsonObject
            {
                ["id"] = "msg_" + Guid.NewGuid().ToString("N"),
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = AnthropicToOpenAiResponse.NonEmpty(StringOrNull(chat, "model")) ?? fallbackModel,
                ["content"] = content,
                ["stop_reason"] = refusal is not null ? "refusal" : StopReasonMap.ToAnthropic(StringOrNull(choice, "finish_reason")),
                ["stop_sequence"] = null,
                ["usage"] = UsageMap.ToAnthropic(chat["usage"])
            };
        }

        private static string? NonEmptyString(JsonNode? node)
            => node is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text) ? text : null;
    }
}
