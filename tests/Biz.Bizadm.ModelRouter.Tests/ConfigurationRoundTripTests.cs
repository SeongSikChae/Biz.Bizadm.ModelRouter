using Biz.Bizadm.ModelRouter.Server;
using YamlDotNet.Serialization;

namespace Biz.Bizadm.ModelRouter.Tests;

[TestClass]
public sealed class ConfigurationRoundTripTests
{
    [TestMethod]
    public void SerializeThenDeserialize_KeepsProvidersAndOmitsNulls()
    {
        Configuration original = ProviderSupportTests.DeserializeConfiguration(ProviderSupportTests.CreateYaml("""
            Providers:
              foundry:
                Type: AzureFoundry
                Endpoint: https://res.services.ai.azure.com
                Key: DEKENC:abc
                Models:
                  - Name: foundry-claude
                    UpstreamModel: claude-sonnet-5-5
                    Api: Anthropic
                  - Name: gpt-6-luna
              openai:
                Type: OpenAI
                Endpoint: https://api.openai.com
                Key: DEKENC:def
                Models:
                  - Name: gpt-6.1-sol
            """));

        ISerializer serializer = Program.CreateConfigurationSerializer();
        string yaml = serializer.Serialize(original);

        StringAssert.DoesNotMatch(yaml, new System.Text.RegularExpressions.Regex(@"UpstreamModel:\s*$|Api:\s*$|:\s*null", System.Text.RegularExpressions.RegexOptions.Multiline));

        Configuration reloaded = ProviderSupportTests.DeserializeConfiguration(yaml);

        Assert.AreEqual(2, reloaded.Providers.Count);
        Assert.AreEqual("DEKENC:abc", reloaded.Providers["foundry"].Key);
        Assert.AreEqual("DEKENC:def", reloaded.Providers["openai"].Key);
        Assert.AreEqual(ProviderType.OpenAI, reloaded.Providers["openai"].Type);
        Assert.AreEqual(ApiProtocol.Anthropic, reloaded.Providers["foundry"].Models![0].Api);
        Assert.IsNull(reloaded.Providers["foundry"].Models![1].Api);
        Assert.IsNull(reloaded.Providers["foundry"].Models![1].UpstreamModel);
    }
}
