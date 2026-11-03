using System.Configuration;
using System.Configuration.Annotation;

namespace Biz.Bizadm.ModelRouter.Server
{
    public enum Provider
    {
        AzureFoundry = 0,
        OpenAI = 1
    }

    public sealed class Configuration : IValidatableConfiguration
    {
        [Property(PropertyType.STRING, required: true)]
        public string ContentRootPath { get; set; } = null!;

        [Property(PropertyType.USHORT, required: true)]
        public ushort? Port { get; set; }

        public SslConfiguration? Ssl { get; set; }

        public OpenAIConfiguration OpenAI { get; set; } = null!;

        public KmsConfiguration Kms { get; set; } = null!;

        /// <summary>
        /// 모델별 reasoning 정책 (권장).
        /// YAML 예:
        /// ReasoningByModel:
        ///   Bizadm-GPT-6-LUNA:
        ///     Effort: medium
        ///     ChatToolsMode: BridgeToResponses
        ///     SupportsNone: true
        ///   Bizadm-GPT-6.1-SOL:
        ///     Effort: medium
        ///     ChatToolsMode: BridgeToResponses
        ///     SupportsNone: false
        /// </summary>
        public Dictionary<string, ModelReasoningOptions>? ReasoningByModel { get; set; }

        /// <summary>
        /// 하위 호환: 모델명 → effort 문자열 맵.
        /// tools가 있으면 BridgeToResponses로 취급한다.
        /// YAML 예:
        /// ReasoningEffortByModel:
        ///   Bizadm-GPT-6-LUNA: medium
        /// </summary>
        public Dictionary<string, string>? ReasoningEffortByModel { get; set; }

        public void Validate()
        {
            if (Ssl is not null)
                ConfigurationValidator.Validate(Ssl);

            if (OpenAI is null)
                throw new Exception("config field 'OpenAI' must be provided");
            ConfigurationValidator.Validate(OpenAI);

            if (Kms is null)
                throw new Exception("config field 'Kms' must be provided");
            ConfigurationValidator.Validate(Kms);
        }

        public sealed class SslConfiguration
        {
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
        }

        public sealed class OpenAIConfiguration : IValidatableConfiguration
        {
            [Property(PropertyType.ENUM, required: false, DefaultValue = "AzureFoundry")]
            public Provider Provider { get; set; } = Provider.AzureFoundry;

            [Property(PropertyType.STRING, required: true)]
            public string OpenAIEndpoint { get; set; } = null!;

            [Property(PropertyType.STRING, required: true)]
            public string OpenAIKey { get; set; } = null!;

            public void Validate()
            {
                if (!Enum.IsDefined(Provider))
                    throw new ArgumentOutOfRangeException(nameof(Provider), Provider, "Unsupported provider.");
            }
        }

        public sealed class KmsConfiguration
        {
            [Property(PropertyType.STRING, required: true)]
            public string DekFilePath { get; set; } = null!;

            [Property(PropertyType.STRING, required: false)]
            public string? KekSalt { get; set; }

            [Property(PropertyType.INT, required: false, DefaultValue = "600000")]
            public int? KekIterations { get; set; }

            [Property(PropertyType.STRING, required: false)]
            public string? CredentialStore { get; set; }
        }
    }
}
