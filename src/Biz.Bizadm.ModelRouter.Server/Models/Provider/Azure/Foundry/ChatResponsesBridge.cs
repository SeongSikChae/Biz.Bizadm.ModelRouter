using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry
{
    /// <summary>
    /// Cursor 등 chat/completions 클라이언트를 위해
    /// Responses API 요청/응답을 chat.completions 형태로 변환한다.
    /// </summary>
    internal static class ChatResponsesBridge
    {
        private static readonly JsonSerializerOptions CompactJson = new()
        {
            WriteIndented = false
        };

        public static byte[] ToResponsesRequest(JsonObject chatRoot, string reasoningEffort)
        {
            JsonObject responses = new JsonObject();

            CopyIfPresent(chatRoot, responses, "model");
            CopyIfPresent(chatRoot, responses, "temperature");
            CopyIfPresent(chatRoot, responses, "top_p");
            CopyIfPresent(chatRoot, responses, "tool_choice");
            CopyIfPresent(chatRoot, responses, "parallel_tool_calls");
            CopyIfPresent(chatRoot, responses, "stream");
            CopyIfPresent(chatRoot, responses, "user");
            CopyIfPresent(chatRoot, responses, "metadata");

            if (chatRoot["max_completion_tokens"] is JsonNode maxCompletion)
                responses["max_output_tokens"] = maxCompletion.DeepClone();
            else if (chatRoot["max_tokens"] is JsonNode maxTokens)
                responses["max_output_tokens"] = maxTokens.DeepClone();

            if (chatRoot["messages"] is JsonArray messages)
            {
                (string? instructions, JsonArray input) = ConvertMessages(messages);
                if (!string.IsNullOrEmpty(instructions))
                    responses["instructions"] = instructions;
                responses["input"] = input;
            }

            if (chatRoot["tools"] is JsonArray tools)
                responses["tools"] = ConvertTools(tools);

            responses["reasoning"] = new JsonObject
            {
                ["effort"] = reasoningEffort
            };

            // Cursor는 chat transcript만 되돌리므로 stateful store는 쓰지 않는다.
            responses["store"] = false;

            return Encoding.UTF8.GetBytes(responses.ToJsonString(CompactJson));
        }

        public static byte[] ToChatCompletionResponse(JsonObject responsesRoot)
        {
            string id = responsesRoot["id"]?.GetValue<string>() ?? $"chatcmpl-{Guid.NewGuid():N}";
            string model = responsesRoot["model"]?.GetValue<string>() ?? string.Empty;
            long created = responsesRoot["created_at"]?.GetValue<long>()
                ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            string? content = null;
            JsonArray? toolCalls = null;

            if (responsesRoot["output"] is JsonArray output)
            {
                foreach (JsonNode? item in output)
                {
                    if (item is not JsonObject itemObject)
                        continue;

                    string? type = itemObject["type"]?.GetValue<string>();
                    if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
                    {
                        content = ExtractOutputText(itemObject);
                    }
                    else if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
                    {
                        toolCalls ??= new JsonArray();
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = itemObject["call_id"]?.DeepClone()
                                ?? itemObject["id"]?.DeepClone()
                                ?? $"call_{toolCalls.Count}",
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = itemObject["name"]?.DeepClone() ?? string.Empty,
                                ["arguments"] = itemObject["arguments"]?.DeepClone() ?? "{}"
                            }
                        });
                    }
                }
            }

            string finishReason = toolCalls is { Count: > 0 } ? "tool_calls" : "stop";
            JsonObject message = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content
            };
            if (toolCalls is { Count: > 0 })
                message["tool_calls"] = toolCalls;

            string chatId = id.StartsWith("chatcmpl-", StringComparison.Ordinal)
                ? id
                : id.StartsWith("resp_", StringComparison.Ordinal)
                    ? $"chatcmpl-{id["resp_".Length..]}"
                    : $"chatcmpl-{id}";

            JsonObject chat = new JsonObject
            {
                ["id"] = chatId,
                ["object"] = "chat.completion",
                ["created"] = created,
                ["model"] = model,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = message,
                        ["finish_reason"] = finishReason
                    }
                }
            };

            if (responsesRoot["usage"] is JsonObject usage)
                chat["usage"] = ConvertUsage(usage);

            return Encoding.UTF8.GetBytes(chat.ToJsonString(CompactJson));
        }

        private sealed class StreamState
        {
            public string CompletionId { get; } = $"chatcmpl-{Guid.NewGuid():N}";
            public long Created { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            public string Model { get; set; } = string.Empty;
            public bool RoleSent { get; set; }
            /// <summary>
            /// Responses 스트림은 item.id(fc_*)와 call_id(call_*)를 섞어 보낸다.
            /// 둘 다 같은 chat tool_calls index로 해석해야 한다.
            /// </summary>
            public Dictionary<string, int> ToolCallIndexes { get; } = new(StringComparer.Ordinal);
            public Dictionary<int, string> ToolCallIdsByIndex { get; } = new();
            public int NextToolIndex { get; set; }
            public string? FinishReason { get; set; }

            public int RegisterFunctionCall(string? itemId, string? callId)
            {
                string chatCallId = !string.IsNullOrWhiteSpace(callId)
                    ? callId
                    : !string.IsNullOrWhiteSpace(itemId)
                        ? itemId
                        : $"call_{NextToolIndex}";

                if (!string.IsNullOrWhiteSpace(callId)
                    && ToolCallIndexes.TryGetValue(callId, out int byCallId))
                {
                    Alias(itemId, byCallId);
                    // chat 쪽 id는 항상 call_id를 우선 (function_call_output 라운드트립)
                    ToolCallIdsByIndex[byCallId] = callId;
                    return byCallId;
                }

                if (!string.IsNullOrWhiteSpace(itemId)
                    && ToolCallIndexes.TryGetValue(itemId, out int byItemId))
                {
                    Alias(callId, byItemId);
                    if (!string.IsNullOrWhiteSpace(callId))
                        ToolCallIdsByIndex[byItemId] = callId;
                    else if (!ToolCallIdsByIndex.ContainsKey(byItemId))
                        ToolCallIdsByIndex[byItemId] = chatCallId;
                    return byItemId;
                }

                int index = NextToolIndex++;
                ToolCallIdsByIndex[index] = chatCallId;
                Alias(callId, index);
                Alias(itemId, index);
                Alias(chatCallId, index);
                return index;
            }

            public bool TryResolveFunctionCallIndex(JsonObject evt, out int index)
            {
                string? callId = evt["call_id"]?.GetValue<string>();
                string? itemId = evt["item_id"]?.GetValue<string>();

                if (!string.IsNullOrWhiteSpace(callId)
                    && ToolCallIndexes.TryGetValue(callId, out index))
                {
                    Alias(itemId, index);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(itemId)
                    && ToolCallIndexes.TryGetValue(itemId, out index))
                {
                    Alias(callId, index);
                    return true;
                }

                // added 이벤트 누락 시에만 새 슬롯 생성
                index = RegisterFunctionCall(itemId, callId);
                return true;
            }

            private void Alias(string? key, int index)
            {
                if (!string.IsNullOrWhiteSpace(key))
                    ToolCallIndexes[key] = index;
            }
        }

        public static async IAsyncEnumerable<string> ToChatCompletionSseAsync(
            Stream responsesSse,
            string? modelHint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamState state = new StreamState
            {
                Model = modelHint ?? string.Empty
            };

            using StreamReader reader = new StreamReader(responsesSse, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            StringBuilder dataBuilder = new StringBuilder();

            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;

                if (line.Length == 0)
                {
                    if (dataBuilder.Length == 0)
                        continue;

                    string data = dataBuilder.ToString();
                    dataBuilder.Clear();

                    if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                        break;

                    JsonNode? node;
                    try
                    {
                        node = JsonNode.Parse(data);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (node is not JsonObject evt)
                        continue;

                    string? eventType = evt["type"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(eventType))
                        continue;

                    if (evt["response"]?["model"] is JsonValue modelValue
                        && modelValue.TryGetValue(out string? responseModel)
                        && !string.IsNullOrWhiteSpace(responseModel))
                    {
                        state.Model = responseModel;
                    }

                    foreach (JsonObject chunk in ConvertStreamEvent(eventType, evt, state))
                        yield return $"data: {chunk.ToJsonString(CompactJson)}\n\n";

                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (dataBuilder.Length > 0)
                        dataBuilder.Append('\n');
                    dataBuilder.Append(line.AsSpan(5).TrimStart());
                }
            }

            JsonObject finalChunk = new JsonObject
            {
                ["id"] = state.CompletionId,
                ["object"] = "chat.completion.chunk",
                ["created"] = state.Created,
                ["model"] = state.Model,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = new JsonObject(),
                        ["finish_reason"] = state.FinishReason ?? "stop"
                    }
                }
            };
            yield return $"data: {finalChunk.ToJsonString(CompactJson)}\n\n";
            yield return "data: [DONE]\n\n";
        }

        private static IEnumerable<JsonObject> ConvertStreamEvent(
            string eventType,
            JsonObject evt,
            StreamState state)
        {
            if (string.Equals(eventType, "response.output_text.delta", StringComparison.Ordinal))
            {
                string? delta = evt["delta"]?.GetValue<string>();
                if (string.IsNullOrEmpty(delta))
                    yield break;

                JsonObject deltaObject = new JsonObject();
                if (!state.RoleSent)
                {
                    deltaObject["role"] = "assistant";
                    state.RoleSent = true;
                }

                deltaObject["content"] = delta;
                yield return Chunk(state.CompletionId, state.Created, state.Model, deltaObject, finishReason: null);
                yield break;
            }

            if (string.Equals(eventType, "response.output_item.added", StringComparison.Ordinal))
            {
                if (evt["item"] is not JsonObject item)
                    yield break;

                string? type = item["type"]?.GetValue<string>();
                if (!string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
                    yield break;

                string? itemId = item["id"]?.GetValue<string>();
                string? callId = item["call_id"]?.GetValue<string>();
                int index = state.RegisterFunctionCall(itemId, callId);
                string chatCallId = state.ToolCallIdsByIndex[index];

                JsonObject deltaObject = new JsonObject();
                if (!state.RoleSent)
                {
                    deltaObject["role"] = "assistant";
                    state.RoleSent = true;
                }

                deltaObject["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = index,
                        ["id"] = chatCallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = item["name"]?.DeepClone() ?? string.Empty,
                            ["arguments"] = string.Empty
                        }
                    }
                };

                state.FinishReason = "tool_calls";
                yield return Chunk(state.CompletionId, state.Created, state.Model, deltaObject, finishReason: null);
                yield break;
            }

            if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.Ordinal))
            {
                string? argsDelta = evt["delta"]?.GetValue<string>();
                if (string.IsNullOrEmpty(argsDelta))
                    yield break;

                state.TryResolveFunctionCallIndex(evt, out int index);

                JsonObject deltaObject = new JsonObject
                {
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = index,
                            ["function"] = new JsonObject
                            {
                                ["arguments"] = argsDelta
                            }
                        }
                    }
                };

                state.FinishReason = "tool_calls";
                yield return Chunk(state.CompletionId, state.Created, state.Model, deltaObject, finishReason: null);
                yield break;
            }

            if (string.Equals(eventType, "response.function_call_arguments.done", StringComparison.Ordinal))
            {
                // delta를 못 받은 경우를 대비해 done의 최종 arguments로 한 번 더 보정하지는 않는다.
                // (delta를 이미 보냈다면 중복 append가 됨)
                // item_id ↔ call_id 별칭만 확정한다.
                state.TryResolveFunctionCallIndex(evt, out _);
                state.FinishReason = "tool_calls";
                yield break;
            }

            if (string.Equals(eventType, "response.completed", StringComparison.Ordinal)
                && state.FinishReason is null)
            {
                state.FinishReason = "stop";
            }
        }

        private static JsonObject Chunk(
            string id,
            long created,
            string model,
            JsonObject delta,
            string? finishReason)
        {
            return new JsonObject
            {
                ["id"] = id,
                ["object"] = "chat.completion.chunk",
                ["created"] = created,
                ["model"] = model,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = delta,
                        ["finish_reason"] = finishReason
                    }
                }
            };
        }

        private static (string? Instructions, JsonArray Input) ConvertMessages(JsonArray messages)
        {
            StringBuilder instructions = new StringBuilder();
            JsonArray input = new JsonArray();

            foreach (JsonNode? node in messages)
            {
                if (node is not JsonObject message)
                    continue;

                string? role = message["role"]?.GetValue<string>();
                if (string.IsNullOrEmpty(role))
                    continue;

                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
                {
                    string text = ExtractTextContent(message["content"]);
                    if (string.IsNullOrEmpty(text))
                        continue;

                    if (instructions.Length > 0)
                        instructions.Append("\n\n");
                    instructions.Append(text);
                    continue;
                }

                if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = ConvertInputContent(message["content"])
                    });
                    continue;
                }

                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    string text = ExtractTextContent(message["content"]);
                    if (!string.IsNullOrEmpty(text))
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "message",
                            ["role"] = "assistant",
                            ["content"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "output_text",
                                    ["text"] = text
                                }
                            }
                        });
                    }

                    if (message["tool_calls"] is JsonArray toolCalls)
                    {
                        foreach (JsonNode? toolCallNode in toolCalls)
                        {
                            if (toolCallNode is not JsonObject toolCall)
                                continue;

                            JsonObject function = toolCall["function"] as JsonObject ?? new JsonObject();
                            input.Add(new JsonObject
                            {
                                ["type"] = "function_call",
                                ["call_id"] = toolCall["id"]?.DeepClone() ?? $"call_{input.Count}",
                                ["name"] = function["name"]?.DeepClone() ?? string.Empty,
                                ["arguments"] = function["arguments"]?.DeepClone() ?? "{}"
                            });
                        }
                    }

                    continue;
                }

                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = message["tool_call_id"]?.DeepClone() ?? string.Empty,
                        ["output"] = ExtractTextContent(message["content"])
                    });
                }
            }

            return (instructions.Length == 0 ? null : instructions.ToString(), input);
        }

        private static JsonArray ConvertTools(JsonArray tools)
        {
            JsonArray converted = new JsonArray();
            foreach (JsonNode? toolNode in tools)
            {
                if (toolNode is not JsonObject tool)
                    continue;

                JsonObject function = tool["function"] as JsonObject ?? tool;
                string? name = function["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                JsonObject convertedTool = new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = name,
                    ["description"] = function["description"]?.DeepClone() ?? string.Empty,
                    ["parameters"] = function["parameters"]?.DeepClone() ?? new JsonObject()
                };

                if (function["strict"] is JsonNode strict)
                    convertedTool["strict"] = strict.DeepClone();

                converted.Add(convertedTool);
            }

            return converted;
        }

        private static JsonNode ConvertInputContent(JsonNode? content)
        {
            if (content is null)
                return new JsonArray();

            if (content is JsonValue value && value.TryGetValue(out string? text))
            {
                return new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = text ?? string.Empty
                    }
                };
            }

            if (content is JsonArray parts)
            {
                JsonArray converted = new JsonArray();
                foreach (JsonNode? part in parts)
                {
                    if (part is not JsonObject partObject)
                        continue;

                    string? type = partObject["type"]?.GetValue<string>();
                    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        converted.Add(new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = partObject["text"]?.DeepClone() ?? string.Empty
                        });
                    }
                    else if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase))
                    {
                        JsonNode? imageUrl = partObject["image_url"];
                        string? url = imageUrl is JsonObject imageObject
                            ? imageObject["url"]?.GetValue<string>()
                            : imageUrl?.GetValue<string>();
                        if (!string.IsNullOrEmpty(url))
                        {
                            converted.Add(new JsonObject
                            {
                                ["type"] = "input_image",
                                ["image_url"] = url
                            });
                        }
                    }
                }

                return converted;
            }

            return new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = content.ToJsonString()
                }
            };
        }

        private static string ExtractTextContent(JsonNode? content)
        {
            if (content is null)
                return string.Empty;

            if (content is JsonValue value && value.TryGetValue(out string? text))
                return text ?? string.Empty;

            if (content is JsonArray parts)
            {
                StringBuilder builder = new StringBuilder();
                foreach (JsonNode? part in parts)
                {
                    if (part is JsonObject partObject
                        && partObject["text"] is JsonValue textValue
                        && textValue.TryGetValue(out string? partText)
                        && !string.IsNullOrEmpty(partText))
                    {
                        builder.Append(partText);
                    }
                }

                return builder.ToString();
            }

            return content.ToJsonString();
        }

        private static string? ExtractOutputText(JsonObject messageItem)
        {
            if (messageItem["content"] is not JsonArray content)
                return null;

            StringBuilder builder = new StringBuilder();
            foreach (JsonNode? part in content)
            {
                if (part is not JsonObject partObject)
                    continue;

                string? type = partObject["type"]?.GetValue<string>();
                if (string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    if (partObject["text"] is JsonValue textValue
                        && textValue.TryGetValue(out string? text)
                        && !string.IsNullOrEmpty(text))
                    {
                        builder.Append(text);
                    }
                }
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private static JsonObject ConvertUsage(JsonObject usage)
        {
            int input = usage["input_tokens"]?.GetValue<int>() ?? 0;
            int output = usage["output_tokens"]?.GetValue<int>() ?? 0;
            return new JsonObject
            {
                ["prompt_tokens"] = input,
                ["completion_tokens"] = output,
                ["total_tokens"] = usage["total_tokens"]?.GetValue<int>() ?? input + output
            };
        }

        private static void CopyIfPresent(JsonObject source, JsonObject target, string propertyName)
        {
            if (source[propertyName] is JsonNode node)
                target[propertyName] = node.DeepClone();
        }
    }
}
