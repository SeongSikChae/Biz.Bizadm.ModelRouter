using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider
{
    /// <summary>
    /// 공급자 Endpoint를 프로토콜별 기준 URL로 정규화한다. 반환값에는 /v1이 포함되지 않는다.
    /// </summary>
    public static class ProviderEndpoints
    {
        private static readonly string[] FoundrySuffixes = ["/openai", "/anthropic"];

        public static string OpenAiBase(Configuration.ProviderConfiguration provider)
            => provider.Type == ProviderType.AzureFoundry
                ? FoundryRoot(provider.Endpoint) + "/openai"
                : AzureFoundryChatClient.NormalizeOpenAIEndpoint(provider.Endpoint);

        public static string AnthropicBase(Configuration.ProviderConfiguration provider)
            => provider.Type == ProviderType.AzureFoundry
                ? FoundryRoot(provider.Endpoint) + "/anthropic"
                : AzureFoundryChatClient.NormalizeOpenAIEndpoint(provider.Endpoint);

        /// <summary>
        /// Foundry 리소스 루트. 끝의 /v1…, /openai, /anthropic을 제거한다.
        /// </summary>
        internal static string FoundryRoot(string endpoint)
        {
            string root = AzureFoundryChatClient.NormalizeOpenAIEndpoint(endpoint);
            foreach (string suffix in FoundrySuffixes)
            {
                if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return root[..^suffix.Length];
            }

            return root;
        }
    }
}
