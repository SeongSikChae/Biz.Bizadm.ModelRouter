namespace Biz.Bizadm.ModelRouter.Server
{
    /// <summary>
    /// 모델별 reasoning effort 및 chat+tools 정책.
    /// </summary>
    public sealed class ModelReasoningOptions
    {
        /// <summary>주입할 reasoning effort (예: none, low, medium, high, xhigh, max).</summary>
        public string? Effort { get; set; }

        /// <summary>
        /// chat/completions + function tools 처리 방식.
        /// ForceNone | BridgeToResponses | PassThrough
        /// </summary>
        public string? ChatToolsMode { get; set; }

        /// <summary>
        /// reasoning_effort=none 지원 여부.
        /// false이면 Effort=none을 허용된 최저 단계(low)로 보정하고, ForceNone은 bridge로 폴백한다.
        /// </summary>
        public bool? SupportsNone { get; set; }
    }
}
