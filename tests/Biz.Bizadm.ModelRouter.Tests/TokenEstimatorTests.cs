using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Conversion;

namespace Biz.Bizadm.ModelRouter.Tests;

[TestClass]
public sealed class TokenEstimatorTests
{
    [TestMethod]
    public void Estimate_CountsTextToolsAndExcludesImagesAndThinking()
    {
        // system 4 + tool(name 1 + description 3 + schema {"type":"object"} 17) + text 8 + tool_use(name 1 + input {"a":1} 7) + tool_result 4 = 45 → ceil(45/4)=12
        JsonObject msg = JsonNode.Parse("""
            {"model":"m","system":"SSSS",
             "tools":[{"name":"f","description":"ddd","input_schema":{"type":"object"}}],
             "messages":[
               {"role":"user","content":"12345678"},
               {"role":"assistant","content":[{"type":"thinking","thinking":"long thinking ignored"},
                                              {"type":"tool_use","id":"t","name":"f","input":{"a":1}}]},
               {"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"rrrr"},
                                         {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAAAAAAAA"}}]}]}
            """)!.AsObject();

        Assert.AreEqual(12, TokenEstimator.EstimateAnthropicInput(msg));
    }

    [TestMethod]
    public void Estimate_EmptyMessagesIsZero()
    {
        Assert.AreEqual(0, TokenEstimator.EstimateAnthropicInput(JsonNode.Parse("""{"model":"m","messages":[]}""")!.AsObject()));
    }

    [TestMethod]
    [DataRow("""{"model":"m"}""")]
    [DataRow("""{"model":"m","messages":[1]}""")]
    [DataRow("""{"model":"m","messages":[{"role":"user","content":5}]}""")]
    public void Estimate_StructureErrorsBecomeConversionException(string json)
    {
        Assert.ThrowsExactly<ConversionException>(() => TokenEstimator.EstimateAnthropicInput(JsonNode.Parse(json)!.AsObject()));
    }
}
