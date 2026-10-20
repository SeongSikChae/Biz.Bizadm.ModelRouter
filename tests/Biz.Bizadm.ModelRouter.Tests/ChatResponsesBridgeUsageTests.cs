using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.OpenAi;
using Biz.Bizadm.ModelRouter.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace Biz.Bizadm.ModelRouter.Tests;

public class ChatResponsesBridgeUsageTests
{
    [Fact]
    public void ToChatCompletionResponse_Usage_PreservesTokenDetailsAndInputTotal()
    {
        JsonObject responsesRoot = CreateResponseWithUsage(CreateUsage());

        byte[] body = ChatResponsesBridge.ToChatCompletionResponse(responsesRoot);
        JsonObject chat = JsonNode.Parse(body)!.AsObject();
        JsonObject usage = chat["usage"]!.AsObject();

        Assert.Equal(1_000, usage["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(100, usage["completion_tokens"]!.GetValue<int>());
        Assert.Equal(1_100, usage["total_tokens"]!.GetValue<int>());
        Assert.Equal(800, usage["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>());
        Assert.Equal(12, usage["prompt_tokens_details"]!["cache_write_tokens"]!.GetValue<int>());
        Assert.Equal("preserved", usage["prompt_tokens_details"]!["provider_extension"]!.GetValue<string>());
        Assert.Equal(25, usage["completion_tokens_details"]!["reasoning_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void ToChatCompletionResponse_Usage_PreservesZeroCachedTokens()
    {
        JsonObject responsesRoot = CreateResponseWithUsage(new JsonObject
        {
            ["input_tokens"] = 4,
            ["output_tokens"] = 2,
            ["total_tokens"] = 6,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 }
        });

        byte[] body = ChatResponsesBridge.ToChatCompletionResponse(responsesRoot);
        JsonObject usage = JsonNode.Parse(body)!["usage"]!.AsObject();

        Assert.Equal(0, usage["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void ToChatCompletionResponse_Usage_DoesNotInventMissingDetailsAndFallsBackToTotal()
    {
        JsonObject responsesRoot = CreateResponseWithUsage(new JsonObject
        {
            ["input_tokens"] = 4,
            ["output_tokens"] = 2,
            ["total_tokens"] = null,
            ["input_tokens_details"] = null,
            ["output_tokens_details"] = null
        });

        byte[] body = ChatResponsesBridge.ToChatCompletionResponse(responsesRoot);
        JsonObject usage = JsonNode.Parse(body)!["usage"]!.AsObject();

        Assert.Equal(4, usage["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(2, usage["completion_tokens"]!.GetValue<int>());
        Assert.Equal(6, usage["total_tokens"]!.GetValue<int>());
        Assert.Null(usage["prompt_tokens_details"]);
        Assert.Null(usage["completion_tokens_details"]);
    }

    [Fact]
    public async Task ToChatCompletionSseAsync_CompletedResponse_PreservesUsageInFinalChunk()
    {
        string sse = $"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"resp_usage\",\"model\":\"m\",\"status\":\"completed\",\"usage\":{CreateUsage().ToJsonString()},\"output\":[]}}}}\n\n";

        List<string> frames = await ReadFramesAsync(sse, includeUsage: true);

        int finishIndex = frames.FindIndex(frame => frame.Contains("\"finish_reason\":\"stop\"", StringComparison.Ordinal));
        int usageIndex = frames.FindIndex(frame => frame.Contains("\"choices\":[]", StringComparison.Ordinal));
        int doneIndex = frames.FindIndex(frame => frame.Contains("[DONE]", StringComparison.Ordinal));
        Assert.True(finishIndex >= 0);
        Assert.True(usageIndex > finishIndex);
        Assert.True(doneIndex > usageIndex);

        JsonObject usageChunk = ParseFrame(frames[usageIndex]);
        JsonObject usage = usageChunk["usage"]!.AsObject();
        Assert.Equal(1_000, usage["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(800, usage["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>());
        Assert.Equal(25, usage["completion_tokens_details"]!["reasoning_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task ToChatCompletionSseAsync_MappedIncomplete_PreservesTopLevelUsageFallback()
    {
        string sse = $"data: {{\"type\":\"response.incomplete\",\"incomplete_details\":{{\"reason\":\"max_output_tokens\"}},\"usage\":{CreateUsage().ToJsonString()}}}\n\n";

        List<string> frames = await ReadFramesAsync(sse, includeUsage: true);

        Assert.Contains(frames, frame => frame.Contains("\"finish_reason\":\"length\"", StringComparison.Ordinal));
        JsonObject usageChunk = ParseFrame(Assert.Single(frames, frame => frame.Contains("\"choices\":[]", StringComparison.Ordinal)));
        Assert.Equal(800, usageChunk["usage"]!["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task ToChatCompletionSseAsync_CompletedResponseWithoutUsage_DoesNotEmitUsageChunk()
    {
        const string sse = "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_no_usage\",\"model\":\"m\",\"status\":\"completed\",\"output\":[]}}\n\n";

        List<string> frames = await ReadFramesAsync(sse, includeUsage: true);

        Assert.DoesNotContain(frames, frame => frame.Contains("\"choices\":[]", StringComparison.Ordinal));
        Assert.DoesNotContain(frames, frame => frame.Contains("\"usage\":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToChatCompletionSseAsync_FailedIncomplete_DoesNotEmitSuccessUsage()
    {
        string sse = $"data: {{\"type\":\"response.incomplete\",\"response\":{{\"id\":\"resp_failed\",\"model\":\"m\",\"status\":\"incomplete\",\"incomplete_details\":{{\"reason\":\"unknown_reason\"}},\"usage\":{CreateUsage().ToJsonString()}}}}}\n\n";

        List<string> frames = await ReadFramesAsync(sse, includeUsage: true);

        Assert.Contains(frames, frame => frame.Contains("\"error\":", StringComparison.Ordinal));
        Assert.DoesNotContain(frames, frame => frame.Contains("\"choices\":[]", StringComparison.Ordinal));
        Assert.DoesNotContain(frames, frame => frame.Contains("\"usage\":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToChatCompletionSseAsync_IncludeUsageFalse_DoesNotEmitUsageChunk()
    {
        string sse = $"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"resp_no_usage\",\"model\":\"m\",\"status\":\"completed\",\"usage\":{CreateUsage().ToJsonString()},\"output\":[]}}}}\n\n";

        List<string> frames = await ReadFramesAsync(sse, includeUsage: false);

        Assert.DoesNotContain(frames, frame => frame.Contains("\"choices\":[]", StringComparison.Ordinal));
        Assert.DoesNotContain(frames, frame => frame.Contains("\"usage\":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatCompletionsToolCallIdRewrite_PreservesUsageDetailsInResponseAndSse()
    {
        const string clientCallId = "call_client";
        ToolCallIdMapper mapper = new();
        JsonObject request = new()
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = clientCallId,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "test", ["arguments"] = "{}" }
                        }
                    }
                }
            }
        };
        Assert.True(mapper.RewriteChatCompletionsRequest(request));
        string providerCallId = request["messages"]![0]!["tool_calls"]![0]!["id"]!.GetValue<string>();

        JsonObject responseBody = new()
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject { ["id"] = providerCallId }
                        }
                    }
                }
            },
            ["usage"] = CreateChatUsage()
        };
        using HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody.ToJsonString(), Encoding.UTF8, "application/json")
        };

        await ToolCallIdResponseRewriter.RewriteHttpContentAsync(
            response,
            mapper,
            isChatCompletions: true,
            CancellationToken.None);

        JsonObject rewritten = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(clientCallId, rewritten["choices"]![0]!["message"]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(800, rewritten["usage"]!["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>());
        Assert.Equal(25, rewritten["usage"]!["completion_tokens_details"]!["reasoning_tokens"]!.GetValue<int>());

        JsonObject streamChunk = new()
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["delta"] = new JsonObject
                    {
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject { ["id"] = providerCallId }
                        }
                    }
                }
            },
            ["usage"] = CreateChatUsage()
        };
        string? rewrittenSsePayload = ToolCallIdResponseRewriter.RewriteSseDataPayload(
            streamChunk.ToJsonString(),
            mapper,
            isChatCompletions: true);
        Assert.NotNull(rewrittenSsePayload);
        JsonObject rewrittenChunk = JsonNode.Parse(rewrittenSsePayload)!.AsObject();
        Assert.Equal(clientCallId, rewrittenChunk["choices"]![0]!["delta"]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(800, rewrittenChunk["usage"]!["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("https://foundry.example/openai", "https://foundry.example/openai")]
    [InlineData("https://foundry.example/openai/", "https://foundry.example/openai")]
    [InlineData("https://foundry.example/openai/v1", "https://foundry.example/openai")]
    [InlineData("https://foundry.example/openai/v1/", "https://foundry.example/openai")]
    [InlineData("https://foundry.example/openai/v1/chat/completions", "https://foundry.example/openai")]
    [InlineData("https://api.openai.com", "https://api.openai.com")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com")]
    public void NormalizeOpenAIEndpoint_StripsFromV1(string input, string expected)
    {
        Assert.Equal(expected, AzureFoundryChatClient.NormalizeOpenAIEndpoint(input));
    }

    [Fact]
    public async Task DirectChatAndResponsesForwarding_PreserveProviderUsagePayloads()
    {
        JsonObject chatUsage = CreateChatUsage();
        JsonObject responsesUsage = CreateUsage();
        using HttpClient httpClient = new(new StaticResponseHandler(request =>
        {
            JsonObject responseBody = request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? new JsonObject { ["usage"] = chatUsage.DeepClone() }
                : new JsonObject { ["usage"] = responsesUsage.DeepClone() };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody.ToJsonString(), Encoding.UTF8, "application/json")
            };
        }));
        AzureFoundryChatClient client = new(
            httpClient,
            new Biz.Bizadm.ModelRouter.Server.Configuration
            {
                OpenAIEndpoint = "https://foundry.example/openai",
                OpenAIKey = "test-key"
            },
            NullLogger<AzureFoundryChatClient>.Instance);

        using HttpResponseMessage chatResponse = await client.ForwardChatCompletionsAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("{\"model\":\"m\",\"messages\":[]}")),
            "application/json",
            CancellationToken.None);
        JsonObject chatBody = JsonNode.Parse(await chatResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(800, chatBody["usage"]!["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>());

        using HttpResponseMessage responsesResponse = await client.ForwardResponsesAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("{\"model\":\"m\",\"input\":\"hello\"}")),
            "application/json",
            CancellationToken.None);
        JsonObject responsesBody = JsonNode.Parse(await responsesResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(800, responsesBody["usage"]!["input_tokens_details"]!["cached_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task ResponseToolCallIdRewrite_PreservesUsageDetails()
    {
        const string clientCallId = "call_client";
        ToolCallIdMapper mapper = new();
        JsonObject request = new()
        {
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = clientCallId,
                    ["output"] = "ok"
                }
            }
        };
        Assert.True(mapper.RewriteResponsesRequest(request));
        string providerCallId = request["input"]![0]!["call_id"]!.GetValue<string>();

        JsonObject originalUsage = CreateUsage();
        JsonObject responseBody = new()
        {
            ["output"] = new JsonArray
            {
                new JsonObject { ["type"] = "function_call", ["call_id"] = providerCallId }
            },
            ["usage"] = originalUsage.DeepClone()
        };
        using HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody.ToJsonString(), Encoding.UTF8, "application/json")
        };

        await ToolCallIdResponseRewriter.RewriteHttpContentAsync(
            response,
            mapper,
            isChatCompletions: false,
            CancellationToken.None);

        JsonObject rewritten = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(clientCallId, rewritten["output"]![0]!["call_id"]!.GetValue<string>());
        Assert.Equal(800, rewritten["usage"]!["input_tokens_details"]!["cached_tokens"]!.GetValue<int>());
        Assert.Equal(25, rewritten["usage"]!["output_tokens_details"]!["reasoning_tokens"]!.GetValue<int>());
    }

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(createResponse(request));
    }

    private static JsonObject CreateChatUsage() => new()
    {
        ["prompt_tokens"] = 1_000,
        ["completion_tokens"] = 100,
        ["total_tokens"] = 1_100,
        ["prompt_tokens_details"] = new JsonObject
        {
            ["cached_tokens"] = 800
        },
        ["completion_tokens_details"] = new JsonObject
        {
            ["reasoning_tokens"] = 25
        }
    };

    private static JsonObject CreateUsage() => new()
    {
        ["input_tokens"] = 1_000,
        ["output_tokens"] = 100,
        ["total_tokens"] = 1_100,
        ["input_tokens_details"] = new JsonObject
        {
            ["cached_tokens"] = 800,
            ["cache_write_tokens"] = 12,
            ["provider_extension"] = "preserved"
        },
        ["output_tokens_details"] = new JsonObject
        {
            ["reasoning_tokens"] = 25
        }
    };

    private static JsonObject CreateResponseWithUsage(JsonObject usage) => new()
    {
        ["id"] = "resp_usage",
        ["created_at"] = 1_700_000_000L,
        ["status"] = "completed",
        ["model"] = "m",
        ["output"] = new JsonArray(),
        ["usage"] = usage
    };

    private static async Task<List<string>> ReadFramesAsync(string sse, bool includeUsage)
    {
        List<string> frames = new();
        await foreach (string frame in ChatResponsesBridge.ToChatCompletionSseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)),
            modelHint: "m",
            includeUsage,
            NullLogger.Instance,
            CancellationToken.None))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static JsonObject ParseFrame(string frame)
        => JsonNode.Parse(frame["data: ".Length..].Trim())!.AsObject();
}
