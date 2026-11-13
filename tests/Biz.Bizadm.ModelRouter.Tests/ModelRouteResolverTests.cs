using Biz.Bizadm.ModelRouter.Server;
using Biz.Bizadm.ModelRouter.Server.Models.Provider;
using Microsoft.Extensions.Logging.Abstractions;

namespace Biz.Bizadm.ModelRouter.Tests;

[TestClass]
public sealed class ModelRouteResolverTests
{
    private static ModelRouteResolver CreateResolver()
    {
        Configuration configuration = new()
        {
            Providers = new Dictionary<string, Configuration.ProviderConfiguration>
            {
                ["foundry"] = ProviderSupportTests.Provider(
                    ProviderType.AzureFoundry,
                    "https://res.services.ai.azure.com",
                    ProviderSupportTests.Model("foundry-claude", "claude-sonnet-5-5", ApiProtocol.Anthropic),
                    ProviderSupportTests.Model("gpt-6-luna")),
                ["anthropic"] = ProviderSupportTests.Provider(
                    ProviderType.Anthropic,
                    "https://api.anthropic.com",
                    ProviderSupportTests.Model("claude-sonnet-5-5"))
            }
        };
        return new ModelRouteResolver(configuration, NullLogger<ModelRouteResolver>.Instance);
    }

    [TestMethod]
    public void Resolve_MatchesNameIgnoringCase()
    {
        ModelRoute? route = CreateResolver().Resolve("GPT-6-Luna");

        Assert.IsNotNull(route);
        Assert.AreEqual("foundry", route.ProviderName);
        Assert.AreEqual(ApiProtocol.OpenAI, route.Api);
        Assert.AreEqual("gpt-6-luna", route.Name);
        Assert.AreEqual("gpt-6-luna", route.UpstreamModel);
    }

    [TestMethod]
    public void Resolve_StripsEffortSuffix()
    {
        ModelRoute? route = CreateResolver().Resolve("foundry-claude-high");

        Assert.IsNotNull(route);
        Assert.AreEqual("foundry", route.ProviderName);
        Assert.AreEqual(ApiProtocol.Anthropic, route.Api);
        Assert.AreEqual("claude-sonnet-5-5", route.UpstreamModel);
    }

    [TestMethod]
    public void Resolve_RoutesSameUpstreamModelToDifferentProviders()
    {
        ModelRouteResolver resolver = CreateResolver();

        Assert.AreEqual("anthropic", resolver.Resolve("claude-sonnet-5-5")!.ProviderName);
        Assert.AreEqual("foundry", resolver.Resolve("foundry-claude")!.ProviderName);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("unknown-model")]
    public void Resolve_ReturnsNullForUnknownModel(string? model)
    {
        Assert.IsNull(CreateResolver().Resolve(model));
    }

    [TestMethod]
    public void FindExact_DoesNotStripEffortSuffix()
    {
        ModelRouteResolver resolver = CreateResolver();

        Assert.IsNotNull(resolver.FindExact("GPT-6-LUNA"));
        Assert.IsNull(resolver.FindExact("gpt-6-luna-high"));
    }

    [TestMethod]
    public void Routes_KeepConfigurationOrder()
    {
        CollectionAssert.AreEqual(
            new[] { "foundry-claude", "gpt-6-luna", "claude-sonnet-5-5" },
            CreateResolver().Routes.Select(route => route.Name).ToArray());
    }
}
