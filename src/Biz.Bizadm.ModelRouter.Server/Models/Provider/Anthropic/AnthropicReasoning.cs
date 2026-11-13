using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider.Anthropic
{
    /// <summary>OpenAI effort 값을 Anthropic thinking·output_config.effort로 적용한다(설계 4.5).</summary>
    internal static class AnthropicReasoning
    {
        public static void ApplyEffort(JsonObject root, string effort)
        {
            string normalized = effort.Trim().ToLowerInvariant();
            if (normalized is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
                return;

            JsonObject outputConfig = root["output_config"] as JsonObject ?? new JsonObject();
            if (normalized == "none")
            {
                outputConfig["effort"] = "low";
                root.Remove("thinking");
            }
            else
            {
                outputConfig["effort"] = normalized == "minimal" ? "low" : normalized;
                root["thinking"] = new JsonObject { ["type"] = "adaptive" };
            }

            root["output_config"] = outputConfig;
        }
    }
}
