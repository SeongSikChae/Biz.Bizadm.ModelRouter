using System.Configuration;
using System.Configuration.Annotation;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;

namespace Biz.Bizadm.ModelRouter.Server
{
    public enum ProviderType
    {
        AzureFoundry = 0,
        OpenAI = 1,
        Anthropic = 2
    }

    public enum ApiProtocol
    {
        OpenAI = 0,
        Anthropic = 1
    }

    public sealed class Configuration : IValidatableConfiguration
    {
        [Property(PropertyType.STRING, required: true)]
        public string ContentRootPath { get; set; } = null!;

        [Property(PropertyType.USHORT, required: true)]
        public ushort? Port { get; set; }

        public SslConfiguration? Ssl { get; set; }

        /// <summary>
        /// 공급자 이름 → 공급자 설정. 요청 model은 모든 공급자의 Models[].Name 중 하나와 일치해야 한다.
        /// </summary>
        public Dictionary<string, ProviderConfiguration> Providers { get; set; } = null!;

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

            if (Providers is null || Providers.Count == 0)
                throw new Exception("config field 'Providers' must contain at least one provider");

            HashSet<string> modelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, ProviderConfiguration? provider) in Providers)
            {
                if (provider is null)
                    throw new Exception($"provider '{name}' must be provided");

                ConfigurationValidator.Validate(provider);
                provider.Validate();

                foreach (ProviderModelConfiguration model in provider.Models!)
                {
                    if (!modelNames.Add(model.Name))
                        throw new Exception($"model '{model.Name}' is defined in more than one provider");
                }
            }

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

        public sealed class ProviderConfiguration
        {
            /// <summary>필수. 생략 시 enum 기본값으로 조용히 바뀌지 않도록 nullable로 둔다.</summary>
            public ProviderType? Type { get; set; }

            [Property(PropertyType.STRING, required: true)]
            public string Endpoint { get; set; } = null!;

            [Property(PropertyType.STRING, required: true)]
            public string Key { get; set; } = null!;

            public List<ProviderModelConfiguration>? Models { get; set; }

            public void Validate()
            {
                if (Type is null || !Enum.IsDefined(Type.Value))
                    throw new ArgumentOutOfRangeException(nameof(Type), Type, "Provider 'Type' must be Anthropic, AzureFoundry or OpenAI.");

                if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new Exception($"provider 'Endpoint' must be an absolute http(s) URI: {Endpoint}");

                if (Models is null || Models.Count == 0)
                    throw new Exception("provider must define at least one model in 'Models'");

                foreach (ProviderModelConfiguration? model in Models)
                {
                    if (model is null || string.IsNullOrWhiteSpace(model.Name))
                        throw new Exception("model 'Name' must be provided");
                    if (AzureFoundryChatClient.TrySplitModelEffortSuffix(model.Name, out _, out _))
                        throw new Exception($"model name '{model.Name}' must not end with an effort suffix (-none, -low, -medium, -high, -xhigh, -max)");
                    ResolveApi(model);
                }
            }

            public ApiProtocol ResolveApi(ProviderModelConfiguration model)
            {
                return (Type!.Value, model.Api) switch
                {
                    (ProviderType.AzureFoundry, ApiProtocol api) => api,
                    (ProviderType.AzureFoundry, null) => ApiProtocol.OpenAI,
                    (ProviderType.OpenAI, null or ApiProtocol.OpenAI) => ApiProtocol.OpenAI,
                    (ProviderType.Anthropic, null or ApiProtocol.Anthropic) => ApiProtocol.Anthropic,
                    _ => throw new Exception($"Api '{model.Api}' is not supported by provider type '{Type}'")
                };
            }
        }

        public sealed class ProviderModelConfiguration
        {
            /// <summary>클라이언트가 요청 model로 보내는 이름. 모든 공급자에서 고유해야 한다.</summary>
            public string Name { get; set; } = null!;

            /// <summary>upstream에 보낼 실제 모델 ID/배포명. 생략하면 Name. 공급자 간 중복 허용.</summary>
            public string? UpstreamModel { get; set; }

            /// <summary>AzureFoundry에서만 선택. 생략 시 Type 기본값.</summary>
            public ApiProtocol? Api { get; set; }

            public string ResolveUpstreamModel()
                => string.IsNullOrWhiteSpace(UpstreamModel) ? Name : UpstreamModel;
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
