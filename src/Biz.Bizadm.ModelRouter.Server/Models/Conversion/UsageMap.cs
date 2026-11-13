using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>usage 양방향 공식 (설계 4.3).</summary>
    public static class UsageMap
    {
        public static JsonObject ToOpenAi(JsonNode? usage)
        {
            long input = Int(usage, "input_tokens");
            long cacheRead = Int(usage, "cache_read_input_tokens");
            long cacheCreation = Int(usage, "cache_creation_input_tokens");
            long output = Int(usage, "output_tokens");
            long prompt = input + cacheRead + cacheCreation;

            return new JsonObject
            {
                ["prompt_tokens"] = prompt,
                ["completion_tokens"] = output,
                ["total_tokens"] = prompt + output,
                ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cacheRead }
            };
        }

        public static JsonObject ToAnthropic(JsonNode? usage)
        {
            long prompt = Int(usage, "prompt_tokens");
            long cached = Int(usage?["prompt_tokens_details"], "cached_tokens");
            long completion = Int(usage, "completion_tokens");

            return new JsonObject
            {
                ["input_tokens"] = Math.Max(0, prompt - cached),
                ["cache_read_input_tokens"] = cached,
                ["output_tokens"] = completion
            };
        }

        internal static long Int(JsonNode? node, string name)
            => node?[name] is JsonValue value && value.TryGetValue(out long number) ? number : 0;
    }
}
