using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Biz.Bizadm.ModelRouter.Tests;

public class ChatResponsesBridgeContentFilterTests
{
    [Fact]
    public void ToChatCompletionResponse_ContentFilter_SetsFinishReasonAndPreservesFilters()
    {
        JsonObject responsesRoot = new()
        {
            ["id"] = "resp_filter1",
            ["object"] = "response",
            ["created_at"] = 1_700_000_000L,
            ["status"] = "incomplete",
            ["model"] = "Bizadm-GPT-6.1-SOL",
            ["incomplete_details"] = new JsonObject
            {
                ["reason"] = "content_filter"
            },
            ["output"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "output_text",
                            ["text"] = "partial"
                        }
                    }
                }
            },
            ["content_filters"] = new JsonArray
            {
                new JsonObject
                {
                    ["source_type"] = "completion",
                    ["blocked"] = true,
                    ["content_filter_results"] = new JsonObject
                    {
                        ["hate"] = new JsonObject
                        {
                            ["filtered"] = true,
                            ["severity"] = "high"
                        },
                        ["violence"] = new JsonObject
                        {
                            ["filtered"] = false,
                            ["severity"] = "safe"
                        }
                    }
                }
            }
        };

        Assert.False(ChatResponsesBridge.TryCreateErrorResponse(responsesRoot, out _));

        byte[] body = ChatResponsesBridge.ToChatCompletionResponse(responsesRoot);
        JsonObject chat = JsonNode.Parse(body)!.AsObject();
        JsonObject choice = chat["choices"]![0]!.AsObject();

        Assert.Equal("content_filter", choice["finish_reason"]?.GetValue<string>());
        Assert.Equal("partial", choice["message"]?["content"]?.GetValue<string>());
        Assert.NotNull(choice["content_filters"]);
        Assert.Equal(true, choice["content_filter_results"]?["hate"]?["filtered"]?.GetValue<bool>());
        Assert.Equal(
            "completion:hate:high",
            ChatResponsesBridge.SummarizeContentFilters(responsesRoot["content_filters"] as JsonArray));
    }

    [Fact]
    public void ToChatCompletionResponse_ContentFilterWithoutFilters_SetsFinishReasonOnly()
    {
        JsonObject responsesRoot = new()
        {
            ["id"] = "resp_filter2",
            ["created_at"] = 1_700_000_000L,
            ["status"] = "incomplete",
            ["model"] = "Bizadm-GPT-6.1-SOL",
            ["incomplete_details"] = new JsonObject { ["reason"] = "content_filter" },
            ["output"] = new JsonArray()
        };

        Assert.False(ChatResponsesBridge.TryCreateErrorResponse(responsesRoot, out _));

        byte[] body = ChatResponsesBridge.ToChatCompletionResponse(responsesRoot);
        JsonObject choice = JsonNode.Parse(body)!["choices"]![0]!.AsObject();

        Assert.Equal("content_filter", choice["finish_reason"]?.GetValue<string>());
        Assert.Null(choice["content_filters"]);
        Assert.Null(choice["content_filter_results"]);
    }

    [Fact]
    public void ToChatCompletionResponse_MaxOutputTokens_SetsFinishReasonLength()
    {
        JsonObject responsesRoot = new()
        {
            ["id"] = "resp_length1",
            ["created_at"] = 1_700_000_000L,
            ["status"] = "incomplete",
            ["model"] = "Bizadm-GPT-6.1-SOL",
            ["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" },
            ["output"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "output_text",
                            ["text"] = "cut off"
                        }
                    }
                }
            }
        };

        Assert.False(ChatResponsesBridge.TryCreateErrorResponse(responsesRoot, out _));

        byte[] body = ChatResponsesBridge.ToChatCompletionResponse(responsesRoot);
        JsonObject choice = JsonNode.Parse(body)!["choices"]![0]!.AsObject();
        Assert.Equal("length", choice["finish_reason"]?.GetValue<string>());
    }

    [Fact]
    public async Task ToChatCompletionSseAsync_ContentFilter_EmitsFinishReasonNotError()
    {
        string sse =
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\n" +
            "data: {\"type\":\"response.incomplete\",\"response\":{" +
            "\"id\":\"resp_s1\",\"model\":\"Bizadm-GPT-6.1-SOL\",\"status\":\"incomplete\"," +
            "\"incomplete_details\":{\"reason\":\"content_filter\"}," +
            "\"content_filters\":[{\"source_type\":\"completion\",\"blocked\":true," +
            "\"content_filter_results\":{\"hate\":{\"filtered\":true,\"severity\":\"high\"}}}]}}\n\n";

        List<string> frames = new();
        await foreach (string frame in ChatResponsesBridge.ToChatCompletionSseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)),
            modelHint: "Bizadm-GPT-6.1-SOL",
            includeUsage: false,
            NullLogger.Instance,
            CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.DoesNotContain(frames, f => f.Contains("\"error\"", StringComparison.Ordinal));
        string? final = frames.LastOrDefault(f =>
            f.Contains("\"finish_reason\"", StringComparison.Ordinal)
            && !f.Contains("\"finish_reason\":null", StringComparison.Ordinal));
        Assert.NotNull(final);
        Assert.Contains("\"finish_reason\":\"content_filter\"", final, StringComparison.Ordinal);
        Assert.Contains("\"content_filters\"", final, StringComparison.Ordinal);
        Assert.Contains("\"content_filter_results\"", final, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToChatCompletionSseAsync_UnknownIncomplete_IncludesReasonInError()
    {
        string sse =
            "data: {\"type\":\"response.incomplete\",\"response\":{" +
            "\"id\":\"resp_s2\",\"model\":\"m\",\"status\":\"incomplete\"," +
            "\"incomplete_details\":{\"reason\":\"content_filter_unknown\"}}}\n\n";

        List<string> frames = new();
        await foreach (string frame in ChatResponsesBridge.ToChatCompletionSseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)),
            modelHint: "m",
            includeUsage: false,
            NullLogger.Instance,
            CancellationToken.None))
        {
            frames.Add(frame);
        }

        string? errorFrame = frames.FirstOrDefault(f => f.Contains("\"error\"", StringComparison.Ordinal));
        Assert.NotNull(errorFrame);
        Assert.Contains("content_filter_unknown", errorFrame, StringComparison.Ordinal);
    }
}
