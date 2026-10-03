using System.Configuration.Annotation;

namespace Biz.Bizadm.ModelRouter.Server
{
    public sealed class Configuration
    {
        [Property(PropertyType.STRING, required: true)]
        public string ContentRootPath { get; set; } = null!;

        [Property(PropertyType.USHORT, required: true)]
        public ushort? Port { get; set; }

        [Property(PropertyType.STRING, required: true)]
        public string ServerCertificate { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string ServerCertificatePassword { get; set; } = null!;

        [Property(PropertyType.STRING, required: false)]
        public string? CertificateChain { get; set; }

        [Property(PropertyType.STRING, required: false)]
        public string? IncludeCipherSuites { get; set; }

        [Property(PropertyType.STRING, required: true)]
        public string OpenAIEndpoint { get; set; } = null!;

        [Property(PropertyType.STRING, required: true)]
        public string OpenAIKey { get; set; } = null!;

        /// <summary>
        /// function tools가 포함된 chat/completions 요청에서 model별 reasoning 처리.
        /// - none: chat/completions에 reasoning_effort=none 강제
        /// - none이 아닌 값: /v1/responses로 우회 후 chat 형식으로 변환
        /// YAML 예 (Luna/Sol 모두 medium으로 일관 설정):
        /// ReasoningEffortByModel:
        ///   Bizadm-GPT-6-LUNA: medium
        ///   Bizadm-GPT-6.1-SOL: medium
        /// </summary>
        public Dictionary<string, string>? ReasoningEffortByModel { get; set; }
    }
}
