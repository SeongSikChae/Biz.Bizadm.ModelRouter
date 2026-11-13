namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>finish_reason ↔ stop_reason (설계 4.3).</summary>
    public static class StopReasonMap
    {
        public static string ToOpenAi(string? stopReason) => stopReason switch
        {
            "max_tokens" or "model_context_window_exceeded" => "length",
            "tool_use" => "tool_calls",
            "refusal" => "content_filter",
            _ => "stop"
        };

        /// <summary>OpenAI는 어느 stop 시퀀스에 걸렸는지 알려주지 않으므로 stop은 항상 end_turn이다.</summary>
        public static string ToAnthropic(string? finishReason) => finishReason switch
        {
            "length" => "max_tokens",
            "tool_calls" or "function_call" => "tool_use",
            "content_filter" => "refusal",
            _ => "end_turn"
        };
    }
}
