using System.Net.Http.Headers;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider
{
    /// <summary>
    /// 공급자·프로토콜별 인증 헤더를 요청 단위로 붙인다. 여러 공급자가 HttpClient를 공유하므로 기본 헤더를 쓰지 않는다.
    /// </summary>
    internal static class ProviderAuthentication
    {
        public const string DefaultAnthropicVersion = "2023-06-01";

        public static void Apply(HttpRequestMessage request, Configuration.ProviderConfiguration provider, ApiProtocol api)
        {
            switch (provider.Type, api)
            {
                case (ProviderType.OpenAI, _):
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.Key);
                    break;
                case (ProviderType.AzureFoundry, ApiProtocol.OpenAI):
                    request.Headers.Remove("api-key");
                    request.Headers.TryAddWithoutValidation("api-key", provider.Key);
                    break;
                default:
                    // Anthropic, AzureFoundry + Anthropic
                    request.Headers.Remove("x-api-key");
                    request.Headers.TryAddWithoutValidation("x-api-key", provider.Key);
                    break;
            }
        }
    }
}
