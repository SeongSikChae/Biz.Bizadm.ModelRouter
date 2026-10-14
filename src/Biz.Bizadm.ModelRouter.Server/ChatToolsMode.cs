namespace Biz.Bizadm.ModelRouter.Server
{
    /// <summary>
    /// chat/completions 요청에 function tools가 있을 때의 처리 방식.
    /// </summary>
    public enum ChatToolsMode
    {
        /// <summary>요청을 그대로(선택적 Effort 주입만) chat/completions로 전달.</summary>
        PassThrough = 0,

        /// <summary>
        /// chat/completions에 reasoning_effort=none 강제.
        /// none을 지원하지 않는 모델에서는 BridgeToResponses로 폴백한다.
        /// </summary>
        ForceNone = 1,

        /// <summary>Responses API로 우회한 뒤 chat.completions 형식으로 응답을 변환.</summary>
        BridgeToResponses = 2
    }
}
