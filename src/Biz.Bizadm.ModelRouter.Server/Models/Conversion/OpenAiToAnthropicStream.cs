using System.Text;
using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>
    /// chat.completion.chunk SSE → Anthropic messages SSE (설계 4.3).
    /// text는 스트리밍하고, tool 인자는 index별로 모았다가 종료 시점에 index 순서로 한 번에 보낸다.
    /// </summary>
    public static class OpenAiToAnthropicStream
    {
        public static async Task ConvertAsync(
            Stream upstream,
            Stream output,
            string fallbackModel,
            AnthropicToolIdMapper ids,
            CancellationToken cancellationToken)
        {
            State state = new State(fallbackModel);

            async Task WriteAsync(string text)
            {
                await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            Task EmitAsync(string type, JsonObject payload)
            {
                payload["type"] = type;
                return WriteAsync($"event: {type}\ndata: {payload.ToJsonString()}\n\n");
            }

            async Task EnsureStartedAsync()
            {
                if (state.Started)
                    return;
                state.Started = true;
                await EmitAsync("message_start", new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["id"] = "msg_" + Guid.NewGuid().ToString("N"),
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["model"] = state.Model,
                        ["content"] = new JsonArray(),
                        ["stop_reason"] = null,
                        ["stop_sequence"] = null,
                        ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
                    }
                });
            }

            async Task FinishAsync()
            {
                await EnsureStartedAsync();
                if (state.TextOpen)
                {
                    await EmitAsync("content_block_stop", new JsonObject { ["index"] = state.NextBlockIndex++ });
                    state.TextOpen = false;
                }

                foreach (ToolState tool in state.Tools.Values)
                {
                    int index = state.NextBlockIndex++;
                    await EmitAsync("content_block_start", new JsonObject
                    {
                        ["index"] = index,
                        ["content_block"] = new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = ids.Restore(tool.Id ?? $"toolu_{Guid.NewGuid():N}"),
                            ["name"] = tool.Name,
                            ["input"] = new JsonObject()
                        }
                    });
                    await EmitAsync("content_block_delta", new JsonObject
                    {
                        ["index"] = index,
                        ["delta"] = new JsonObject
                        {
                            ["type"] = "input_json_delta",
                            ["partial_json"] = tool.Arguments.Length > 0 ? tool.Arguments.ToString() : "{}"
                        }
                    });
                    await EmitAsync("content_block_stop", new JsonObject { ["index"] = index });
                }

                await EmitAsync("message_delta", new JsonObject
                {
                    ["delta"] = new JsonObject
                    {
                        ["stop_reason"] = StopReasonMap.ToAnthropic(state.FinishReason),
                        ["stop_sequence"] = null
                    },
                    ["usage"] = UsageMap.ToAnthropic(state.Usage)
                });
                await EmitAsync("message_stop", new JsonObject());
            }

            try
            {
                await foreach (SseEvent sse in SseReader.ReadAsync(upstream, cancellationToken))
                {
                    if (sse.Data.Trim() == "[DONE]")
                    {
                        await FinishAsync();
                        return;
                    }

                    if (!AnthropicToOpenAiStream.TryParseObject(sse.Data, out JsonObject? chunk))
                        break;

                    if (chunk!["error"] is JsonNode)
                    {
                        await WriteAsync(ErrorConverter.OpenAiToAnthropicStreamEvent(chunk));
                        return;
                    }

                    if (chunk["usage"] is JsonObject usage)
                        state.Usage = usage.DeepClone();

                    if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
                        continue;

                    if (!state.Started
                        && chunk["model"] is JsonValue modelValue
                        && modelValue.TryGetValue(out string? model)
                        && !string.IsNullOrEmpty(model))
                    {
                        state.Model = model;
                    }
                    await EnsureStartedAsync();

                    JsonNode? delta = choice["delta"];
                    if (delta?["content"] is JsonValue contentValue
                        && contentValue.TryGetValue(out string? text)
                        && !string.IsNullOrEmpty(text))
                    {
                        if (!state.TextOpen)
                        {
                            state.TextOpen = true;
                            await EmitAsync("content_block_start", new JsonObject
                            {
                                ["index"] = state.NextBlockIndex,
                                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
                            });
                        }

                        await EmitAsync("content_block_delta", new JsonObject
                        {
                            ["index"] = state.NextBlockIndex,
                            ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text }
                        });
                    }

                    if (delta?["tool_calls"] is JsonArray toolCalls)
                    {
                        foreach (JsonNode? node in toolCalls)
                        {
                            if (node is not JsonObject call)
                                continue;

                            int index = call["index"] is JsonValue indexValue && indexValue.TryGetValue(out int i) ? i : 0;
                            if (!state.Tools.TryGetValue(index, out ToolState? tool))
                            {
                                tool = new ToolState();
                                state.Tools[index] = tool;
                            }

                            // id·name은 처음 받은 값만 쓴다(같은 index에 반복돼도 블록을 새로 만들지 않는다).
                            tool.Id ??= String(call, "id");
                            tool.Name ??= String(call["function"], "name");
                            if (String(call["function"], "arguments") is { } arguments)
                                tool.Arguments.Append(arguments);
                        }
                    }

                    if (String(choice, "finish_reason") is { } finishReason)
                        state.FinishReason = finishReason;
                }

            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // upstream 연결이 전송 중에 끊겼다. 아래 비정상 종료 경로로 알린다.
            }

            // [DONE] 없이 끊겼거나 JSON이 아닌 data를 만났다.
            await WriteAsync(ErrorConverter.AnthropicErrorEvent("api_error", AnthropicToOpenAiStream.StreamEndedMessage));
        }

        private static string? String(JsonNode? node, string name)
            => node?[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text) ? text : null;

        private sealed class State(string model)
        {
            public string Model { get; set; } = model;
            public bool Started { get; set; }
            public bool TextOpen { get; set; }
            public int NextBlockIndex { get; set; }
            public string? FinishReason { get; set; }
            public JsonNode? Usage { get; set; }
            public SortedDictionary<int, ToolState> Tools { get; } = new();
        }

        private sealed class ToolState
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public StringBuilder Arguments { get; } = new();
        }
    }
}
