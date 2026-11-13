using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider
{
    public sealed record ModelRoute(
        string ProviderName,
        Configuration.ProviderConfiguration Provider,
        ApiProtocol Api,
        string Name,
        string UpstreamModel);

    /// <summary>
    /// 요청 model(클라이언트용 Name)을 공급자·프로토콜·upstream 모델로 해석한다. 미등록이면 null.
    /// </summary>
    public sealed class ModelRouteResolver
    {
        private readonly Dictionary<string, ModelRoute> _byModel = new(StringComparer.OrdinalIgnoreCase);

        public ModelRouteResolver(Configuration configuration, ILogger<ModelRouteResolver> logger)
        {
            List<ModelRoute> routes = new List<ModelRoute>();
            foreach ((string providerName, Configuration.ProviderConfiguration provider) in configuration.Providers)
            {
                foreach (Configuration.ProviderModelConfiguration model in provider.Models!)
                {
                    ModelRoute route = new ModelRoute(
                        providerName,
                        provider,
                        provider.ResolveApi(model),
                        model.Name,
                        model.ResolveUpstreamModel());
                    routes.Add(route);
                    _byModel[model.Name] = route;
                }
            }

            Routes = routes;

            if (configuration.ReasoningByModel is { Count: > 0 })
            {
                foreach (string key in configuration.ReasoningByModel.Keys)
                {
                    if (!_byModel.ContainsKey(key.Trim()))
                    {
                        logger.LogWarning(
                            "ReasoningByModel key '{Key}' does not match any Providers[*].Models[].Name; the policy will not be applied",
                            key);
                    }
                }
            }
        }

        /// <summary>설정 순서의 전체 라우트.</summary>
        public IReadOnlyList<ModelRoute> Routes { get; }

        /// <summary>요청 라우팅용. effort 접미사를 뗀 뒤 Name과 대소문자 무시 비교한다.</summary>
        public ModelRoute? Resolve(string? requestedModel)
        {
            AzureFoundryChatClient.TrySplitModelEffortSuffix(requestedModel, out string? baseModel, out _);
            return _byModel.GetValueOrDefault(baseModel ?? string.Empty);
        }

        /// <summary>모델 단일 조회용. Name 정확 일치(대소문자 무시), 접미사를 떼지 않는다.</summary>
        public ModelRoute? FindExact(string? name)
            => _byModel.GetValueOrDefault(name ?? string.Empty);
    }
}
