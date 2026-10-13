using System.Configuration.Annotation;

namespace Biz.Bizadm.ModelRouter.Server
{
    public sealed class Configuration
    {
        [Property(PropertyType.STRING, required: true)]
        public string ContentRootPath { get; set; } = null!;

        [Property(PropertyType.USHORT, required: true)]
        public ushort? Port { get; set; }

        [Property(PropertyType.BOOL, DefaultValue = "false")]
        public bool? UseSsl { get; set; } = null!;

        [Property(PropertyType.STRING, required: true, Parent = "UseSsl")]
        public string ServerCertificate { get; set; } = null!;

        [Property(PropertyType.STRING, required: true, Parent = "UseSsl")]
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
        /// 모델별 reasoning effort 주입 (선택).
        /// - chat/completions: 최상위 reasoning_effort (function tools가 있으면 맵 값과 무관하게 none 강제)
        /// - responses: reasoning.effort
        /// YAML 예:
        /// ReasoningEffortByModel:
        ///   Bizadm-GPT-6-LUNA: medium
        ///   Bizadm-GPT-6.1-SOL: medium
        /// </summary>
        public Dictionary<string, string>? ReasoningEffortByModel { get; set; }
    }
}

