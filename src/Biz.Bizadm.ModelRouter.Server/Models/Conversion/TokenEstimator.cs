using System.Text.Json.Nodes;
using static Biz.Bizadm.ModelRouter.Server.Models.Conversion.JsonNodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>
    /// OpenAI 라우트의 /v1/messages/count_tokens 근사치(설계 D8): 텍스트 문자 수 / 4 (올림).
    /// 이미지·thinking 블록은 세지 않는다. 한국어 등은 과소추정될 수 있다.
    /// </summary>
    public static class TokenEstimator
    {
        public static int EstimateAnthropicInput(JsonObject msg)
            => ConversionException.Guard(() => EstimateCore(msg));

        private static int EstimateCore(JsonObject msg)
        {
            long chars = TextOf(msg["system"]).Length;

            if (msg["tools"] is JsonArray tools)
            {
                foreach (JsonNode? node in tools)
                {
                    JsonObject tool = RequireObject(node, "tools[] item");
                    chars += (GetString(tool, "name")?.Length ?? 0)
                        + (GetString(tool, "description")?.Length ?? 0)
                        + (tool["input_schema"]?.ToJsonString().Length ?? 0);
                }
            }

            foreach (JsonNode? node in RequireArray(msg, "messages"))
            {
                JsonObject message = RequireObject(node, "messages[] item");
                _ = GetString(message, "role") ?? throw new ConversionException("messages[].role must be provided.");
                chars += ContentLength(message["content"]);
            }

            return (int)Math.Min(int.MaxValue, (chars + 3) / 4);
        }

        private static long ContentLength(JsonNode? content)
        {
            switch (content)
            {
                case null:
                    return 0;
                case JsonValue value when value.TryGetValue(out string? text):
                    return text?.Length ?? 0;
                case JsonArray blocks:
                    long total = 0;
                    foreach (JsonNode? node in blocks)
                    {
                        JsonObject block = RequireObject(node, "content block");
                        total += GetString(block, "type") switch
                        {
                            "text" => GetString(block, "text")?.Length ?? 0,
                            "tool_use" => block["input"]?.ToJsonString().Length ?? 0,
                            "tool_result" => TextOf(block["content"]).Length,
                            _ => 0
                        };
                    }
                    return total;
                default:
                    throw new ConversionException("content must be a string or an array.");
            }
        }
    }
}
