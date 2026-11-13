using Biz.Bizadm.ModelRouter.Server.Models.Provider.Anthropic;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider
{
    /// <summary>
    /// 라우트별 upstream 클라이언트를 만든다. 불변 상태(정책 lookup)만 보관하고 HttpClient는 호출마다 팩토리에서 받는다.
    /// </summary>
    public sealed class ProviderClientRegistry
    {
        public const string HttpClientName = "upstream";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IReadOnlyDictionary<string, AzureFoundryChatClient.ModelReasoningPolicy> _reasoningByModel;

        public ProviderClientRegistry(
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            Configuration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _reasoningByModel = AzureFoundryChatClient.BuildReasoningLookup(configuration);
        }

        public AzureFoundryChatClient GetOpenAi(ModelRoute route)
            => new AzureFoundryChatClient(
                _httpClientFactory.CreateClient(HttpClientName),
                route.Provider,
                _reasoningByModel,
                _loggerFactory.CreateLogger<AzureFoundryChatClient>());

        public AnthropicMessagesClient GetAnthropic(ModelRoute route)
            => new AnthropicMessagesClient(
                _httpClientFactory.CreateClient(HttpClientName),
                route.Provider,
                _loggerFactory.CreateLogger<AnthropicMessagesClient>());
    }
}
