using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

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

            if (TryCreateErrorResponse(responsesRoot, out byte[] errorResponse))
                return errorResponse;

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

            string? incompleteReason = GetIncompleteReason(responsesRoot);
            string finishReason = ResolveFinishReason(incompleteReason, toolCalls is { Count: > 0 });
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

            JsonObject choice = new JsonObject
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = finishReason
            };
            AttachContentFilters(choice, GetContentFilters(responsesRoot));

            JsonObject chat = new JsonObject
            {
                ["id"] = chatId,
                ["object"] = "chat.completion",
                ["created"] = created,
                ["model"] = model,
                ["choices"] = new JsonArray { choice }
            };

            if (responsesRoot["usage"] is JsonObject usage)
                chat["usage"] = ConvertUsage(usage);

            return Encoding.UTF8.GetBytes(chat.ToJsonString(CompactJson));
        }

        public static bool TryCreateErrorResponse(JsonObject responsesRoot, out byte[] errorBody)
        {
            string? status = responsesRoot["status"]?.GetValue<string>();
            if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
            {
                errorBody = Array.Empty<byte>();
                return false;
            }

            // content_filter / max_output_tokens incomplete는 chat finish_reason으로 변환한다.
            if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase)
                && IsMappedIncompleteReason(GetIncompleteReason(responsesRoot)))
            {
                errorBody = Array.Empty<byte>();
                return false;
            }

            JsonObject? error = GetErrorObject(responsesRoot, status);
            string? incompleteReason = GetIncompleteReason(responsesRoot);
            errorBody = SerializeError(error, status, incompleteReason);
            return true;
        }

        private static JsonObject? GetErrorObject(JsonObject source, string? status)
        {
            JsonNode? errorNode = source["error"] ?? source["response"]?["error"];
            if (errorNode is JsonObject errorObject)
                return (JsonObject)errorObject.DeepClone();

            return null;
        }

        private static byte[] SerializeError(JsonObject? error, string? status, string? fallbackMessage = null)
        {
            string message = error?["message"]?.GetValue<string>()
                ?? fallbackMessage
                ?? $"Responses request {status ?? "failed"}.";
            string type = error?["type"]?.GetValue<string>()
                ?? (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase)
                    ? "incomplete_response"
                    : "server_error");

            JsonObject normalizedError = new JsonObject
            {
                ["message"] = message,
                ["type"] = type
            };
            if (error?["code"] is JsonNode code)
                normalizedError["code"] = code.DeepClone();
            if (error?["param"] is JsonNode param)
                normalizedError["param"] = param.DeepClone();

            return Encoding.UTF8.GetBytes(new JsonObject { ["error"] = normalizedError }.ToJsonString(CompactJson));
        }

        private sealed class StreamState
        {
            public string? TerminalType { get; set; }
            public string? ErrorMessage { get; set; }
            public string? ErrorType { get; set; }
            public string? ErrorCode { get; set; }
            public string? IncompleteReason { get; set; }
            public bool SentError { get; set; }
            public int EventCount { get; set; }
            public int ChunkCount { get; set; }
            public int ParseErrorCount { get; set; }

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
            public JsonObject? Usage { get; set; }
            public JsonArray? ContentFilters { get; set; }

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

        /// <summary>
        /// 출력 청크 전에 도착한 rate-limit 실패를 HTTP 429로 올릴 수 있게 Responses SSE를 선조회한다.
        /// rate-limit이 아니면 소비한 바이트를 앞에 붙인 스트림을 반환해 기존 브리지를 그대로 사용한다.
        /// </summary>
        public static async Task<EarlyStreamPeekResult> PeekEarlyRateLimitFailureAsync(
            Stream responsesSse,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            const int maxPeekBytes = 64 * 1024;
            MemoryStream peeked = new MemoryStream();
            byte[] readBuffer = new byte[8192];
            int searchFrom = 0;
            int parsedEvents = 0;

            while (peeked.Length < maxPeekBytes)
            {
                int read = await responsesSse.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken);
                if (read == 0)
                    break;

                peeked.Write(readBuffer, 0, read);

                while (TryReadNextSseData(peeked, ref searchFrom, out string? data))
                {
                    if (data is null)
                        continue;

                    if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                        return EarlyStreamPeekResult.Continue(CreatePrefixedStream(peeked, responsesSse));

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

                    parsedEvents++;
                    string? eventType = evt["type"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(eventType))
                        continue;

                    if (IsBridgeProgressEvent(eventType, evt))
                        return EarlyStreamPeekResult.Continue(CreatePrefixedStream(peeked, responsesSse));

                    if (!IsTerminalEvent(eventType))
                        continue;

                    bool mappedIncomplete = eventType == "response.incomplete"
                        && IsMappedIncompleteReason(GetIncompleteReason(evt["response"] as JsonObject ?? evt));

                    if (eventType == "response.completed" || mappedIncomplete)
                        return EarlyStreamPeekResult.Continue(CreatePrefixedStream(peeked, responsesSse));

                    string? errorMessage = GetEventErrorMessage(evt);
                    string? errorType = GetEventErrorValue(evt, "type");
                    string? errorCode = GetEventErrorValue(evt, "code");

                    if (!IsRateLimitError(errorCode, errorType, errorMessage))
                        return EarlyStreamPeekResult.Continue(CreatePrefixedStream(peeked, responsesSse));

                    byte[] errorBody = SerializeError(
                        BuildErrorObject(errorMessage, errorType, errorCode),
                        status: "failed");

                    logger.LogWarning(
                        "Bridge SSE early rate-limit failure Type={Type} Code={Code} Detail={Detail} PeekedEvents={PeekedEvents} PeekedBytes={PeekedBytes}",
                        eventType,
                        errorCode,
                        errorMessage,
                        parsedEvents,
                        peeked.Length);

                    await responsesSse.DisposeAsync();
                    await peeked.DisposeAsync();
                    return EarlyStreamPeekResult.RateLimit(errorBody);
                }
            }

            logger.LogDebug(
                "Bridge SSE early peek ended without rate-limit decision PeekedEvents={PeekedEvents} PeekedBytes={PeekedBytes}",
                parsedEvents,
                peeked.Length);
            return EarlyStreamPeekResult.Continue(CreatePrefixedStream(peeked, responsesSse));
        }

        internal readonly struct EarlyStreamPeekResult
        {
            public byte[]? RateLimitErrorBody { get; private init; }
            public Stream? ContinueStream { get; private init; }

            public static EarlyStreamPeekResult RateLimit(byte[] errorBody) => new()
            {
                RateLimitErrorBody = errorBody
            };

            public static EarlyStreamPeekResult Continue(Stream stream) => new()
            {
                ContinueStream = stream
            };
        }

        private static Stream CreatePrefixedStream(MemoryStream peeked, Stream remainder)
        {
            byte[] prefix = peeked.ToArray();
            peeked.Dispose();
            if (prefix.Length == 0)
                return remainder;

            return new PrefixedStream(prefix, remainder);
        }

        private static bool TryReadNextSseData(MemoryStream buffer, ref int searchFrom, out string? data)
        {
            data = null;
            byte[] bytes = buffer.GetBuffer();
            int length = (int)buffer.Length;

            for (int i = searchFrom; i < length - 1; i++)
            {
                int delimiterLength = GetSseEventDelimiterLength(bytes, i, length);
                if (delimiterLength == 0)
                    continue;

                string block = Encoding.UTF8.GetString(bytes, searchFrom, i - searchFrom);
                searchFrom = i + delimiterLength;

                StringBuilder dataBuilder = new StringBuilder();
                using StringReader reader = new StringReader(block);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue;

                    if (dataBuilder.Length > 0)
                        dataBuilder.Append('\n');
                    dataBuilder.Append(line.AsSpan(5).TrimStart());
                }

                if (dataBuilder.Length == 0)
                {
                    data = null;
                    return true;
                }

                data = dataBuilder.ToString();
                return true;
            }

            return false;
        }

        private static int GetSseEventDelimiterLength(byte[] bytes, int index, int length)
        {
            if (bytes[index] == (byte)'\n' && bytes[index + 1] == (byte)'\n')
                return 2;

            if (bytes[index] == (byte)'\r'
                && bytes[index + 1] == (byte)'\n'
                && index + 3 < length
                && bytes[index + 2] == (byte)'\r'
                && bytes[index + 3] == (byte)'\n')
            {
                return 4;
            }

            return 0;
        }

        private static bool IsBridgeProgressEvent(string eventType, JsonObject evt)
        {
            if (string.Equals(eventType, "response.output_text.delta", StringComparison.Ordinal)
                || string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(eventType, "response.output_item.added", StringComparison.Ordinal))
                return false;

            string? itemType = evt["item"]?["type"]?.GetValue<string>();
            return string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsRateLimitError(string? code, string? type, string? message)
        {
            static bool Contains(string? value, string token) =>
                !string.IsNullOrEmpty(value)
                && value.Contains(token, StringComparison.OrdinalIgnoreCase);

            if (Contains(code, "rate_limit")
                || Contains(code, "RateLimit")
                || string.Equals(code, "429", StringComparison.Ordinal))
            {
                return true;
            }

            if (Contains(type, "rate_limit")
                || string.Equals(type, "tokens", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return Contains(message, "rate limit")
                || Contains(message, "RateLimitReached")
                || Contains(message, "token rate limit")
                || Contains(message, "tokens per minute")
                || Contains(message, "TPM");
        }

        private static JsonObject BuildErrorObject(string? message, string? type, string? code)
        {
            JsonObject error = new JsonObject
            {
                ["message"] = message ?? "Token rate limit exceeded.",
                ["type"] = type ?? "rate_limit_exceeded"
            };
            if (!string.IsNullOrEmpty(code))
                error["code"] = code;
            return error;
        }

        private sealed class PrefixedStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly Stream _remainder;
            private int _prefixOffset;
            private bool _disposed;

            public PrefixedStream(byte[] prefix, Stream remainder)
            {
                _prefix = prefix;
                _remainder = remainder;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _remainder.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                int fromPrefix = ReadPrefix(buffer.AsSpan(offset, count));
                if (fromPrefix > 0)
                    return fromPrefix;

                return _remainder.Read(buffer, offset, count);
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                int fromPrefix = ReadPrefix(buffer.AsSpan(offset, count));
                if (fromPrefix > 0)
                    return fromPrefix;

                return await _remainder.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int fromPrefix = ReadPrefix(buffer.Span);
                if (fromPrefix > 0)
                    return ValueTask.FromResult(fromPrefix);

                return _remainder.ReadAsync(buffer, cancellationToken);
            }

            private int ReadPrefix(Span<byte> destination)
            {
                int remaining = _prefix.Length - _prefixOffset;
                if (remaining <= 0 || destination.Length == 0)
                    return 0;

                int toCopy = Math.Min(remaining, destination.Length);
                _prefix.AsSpan(_prefixOffset, toCopy).CopyTo(destination);
                _prefixOffset += toCopy;
                return toCopy;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _remainder.Dispose();
                    _disposed = true;
                }

                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    await _remainder.DisposeAsync();
                    _disposed = true;
                }

                await base.DisposeAsync();
            }
        }

        public static async IAsyncEnumerable<string> ToChatCompletionSseAsync(
            Stream responsesSse,
            string? modelHint,
            bool includeUsage,
            ILogger logger,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamState state = new StreamState { Model = modelHint ?? string.Empty };
            Stopwatch stopwatch = Stopwatch.StartNew();
            logger.LogInformation("Bridge SSE start Model={Model}", state.Model);

            using StreamReader reader = new StreamReader(responsesSse, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            StringBuilder dataBuilder = new StringBuilder();
            bool upstreamDone = false;
            try
            {
                while (!upstreamDone)
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
                        {
                            upstreamDone = true;
                            break;
                        }

                        JsonNode? node;
                        try
                        {
                            node = JsonNode.Parse(data);
                        }
                        catch (JsonException)
                        {
                            state.ParseErrorCount++;
                            continue;
                        }

                        if (node is not JsonObject evt)
                            continue;

                        state.EventCount++;
                        string? eventType = evt["type"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(eventType))
                            continue;

                        if (evt["response"]?["model"] is JsonValue modelValue
                            && modelValue.TryGetValue(out string? responseModel)
                            && !string.IsNullOrWhiteSpace(responseModel))
                            state.Model = responseModel;

                        bool isTerminal = IsTerminalEvent(eventType);
                        if (isTerminal)
                        {
                            state.TerminalType = eventType;
                            state.ErrorMessage = GetEventErrorMessage(evt);
                            state.ErrorType = GetEventErrorValue(evt, "type");
                            state.ErrorCode = GetEventErrorValue(evt, "code");
                            state.IncompleteReason = GetIncompleteReason(evt["response"] as JsonObject ?? evt);
                            if (eventType is "response.completed" or "response.incomplete")
                            {
                                JsonObject? responseUsage = evt["response"]?["usage"] as JsonObject
                                    ?? evt["usage"] as JsonObject;
                                if (responseUsage is not null)
                                    state.Usage = ConvertUsage(responseUsage);

                                JsonArray? contentFilters = GetContentFilters(evt["response"] as JsonObject ?? evt);
                                if (contentFilters is not null)
                                    state.ContentFilters = contentFilters;
                            }
                            if (eventType is "response.failed" or "error")
                                logger.LogWarning("Bridge SSE terminal Type={Type} Detail={Detail}", eventType, state.ErrorMessage ?? state.ErrorCode);
                            else if (eventType == "response.incomplete")
                            {
                                string? filterSummary = SummarizeContentFilters(state.ContentFilters);
                                if (!string.IsNullOrEmpty(filterSummary))
                                    logger.LogWarning(
                                        "Bridge SSE terminal Type={Type} Detail={Detail} ContentFilters={ContentFilters}",
                                        eventType,
                                        state.IncompleteReason ?? state.ErrorMessage,
                                        filterSummary);
                                else
                                    logger.LogWarning("Bridge SSE terminal Type={Type} Detail={Detail}", eventType, state.IncompleteReason ?? state.ErrorMessage);
                            }
                            else
                                logger.LogInformation("Bridge SSE terminal Type={Type}", eventType);
                        }

                        foreach (JsonObject chunk in ConvertStreamEvent(eventType, evt, state))
                        {
                            state.ChunkCount++;
                            if (state.ChunkCount == 1)
                                logger.LogInformation("Bridge SSE firstDelta Kind={Kind}", chunk["choices"]?[0]?["delta"]?["tool_calls"] is not null ? "tool_calls" : "content");
                            yield return $"data: {chunk.ToJsonString(CompactJson)}\n\n";
                        }

                        if (isTerminal && eventType != "response.completed")
                        {
                            upstreamDone = true;
                            break;
                        }
                        if (eventType == "response.completed")
                        {
                            upstreamDone = true;
                            break;
                        }
                        continue;
                    }

                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (dataBuilder.Length > 0)
                            dataBuilder.Append('\n');
                        dataBuilder.Append(line.AsSpan(5).TrimStart());
                    }
                }

                bool mappedIncomplete = state.TerminalType == "response.incomplete"
                    && IsMappedIncompleteReason(state.IncompleteReason);
                bool failed = state.TerminalType is "response.failed" or "error"
                    || state.TerminalType == "response.incomplete" && !mappedIncomplete
                    || state.TerminalType is null;

                if (failed)
                {
                    state.SentError = true;
                    string message = state.ErrorMessage
                        ?? (state.TerminalType is null
                            ? "Responses stream ended before a terminal event."
                            : !string.IsNullOrEmpty(state.IncompleteReason)
                                ? $"Responses request was incomplete ({state.IncompleteReason})."
                                : "Responses request was incomplete.");
                    JsonObject error = new JsonObject
                    {
                        ["message"] = message,
                        ["type"] = state.ErrorType ?? (state.TerminalType == "response.incomplete" ? "incomplete_response" : "server_error")
                    };
                    if (!string.IsNullOrEmpty(state.ErrorCode))
                        error["code"] = state.ErrorCode;
                    yield return $"data: {new JsonObject { ["error"] = error }.ToJsonString(CompactJson)}\n\n";
                }
                else
                {
                    if (mappedIncomplete)
                        state.FinishReason = ResolveFinishReason(state.IncompleteReason, hasToolCalls: state.FinishReason == "tool_calls");

                    JsonObject finalChoice = new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = new JsonObject(),
                        ["finish_reason"] = state.FinishReason ?? "stop"
                    };
                    AttachContentFilters(finalChoice, state.ContentFilters);

                    JsonObject finalChunk = new JsonObject
                    {
                        ["id"] = state.CompletionId,
                        ["object"] = "chat.completion.chunk",
                        ["created"] = state.Created,
                        ["model"] = state.Model,
                        ["choices"] = new JsonArray { finalChoice }
                    };
                    state.ChunkCount++;
                    yield return $"data: {finalChunk.ToJsonString(CompactJson)}\n\n";

                    if (includeUsage && state.Usage is not null)
                    {
                        JsonObject usageChunk = new JsonObject
                        {
                            ["id"] = state.CompletionId,
                            ["object"] = "chat.completion.chunk",
                            ["created"] = state.Created,
                            ["model"] = state.Model,
                            ["choices"] = new JsonArray(),
                            ["usage"] = state.Usage.DeepClone()
                        };
                        state.ChunkCount++;
                        yield return $"data: {usageChunk.ToJsonString(CompactJson)}\n\n";
                    }
                }

                yield return "data: [DONE]\n\n";
            }
            finally
            {
                string? filterSummary = SummarizeContentFilters(state.ContentFilters);
                if (!string.IsNullOrEmpty(filterSummary))
                {
                    logger.LogInformation(
                        "Bridge SSE end Model={Model} Events={Events} Chunks={Chunks} FinishReason={FinishReason} Terminal={Terminal} SentError={SentError} ContentFilters={ContentFilters} ParseErrors={ParseErrors} ElapsedMs={ElapsedMs}",
                        state.Model, state.EventCount, state.ChunkCount, state.FinishReason, state.TerminalType, state.SentError, filterSummary, state.ParseErrorCount, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    logger.LogInformation(
                        "Bridge SSE end Model={Model} Events={Events} Chunks={Chunks} FinishReason={FinishReason} Terminal={Terminal} SentError={SentError} ParseErrors={ParseErrors} ElapsedMs={ElapsedMs}",
                        state.Model, state.EventCount, state.ChunkCount, state.FinishReason, state.TerminalType, state.SentError, state.ParseErrorCount, stopwatch.ElapsedMilliseconds);
                }
            }
        }

        private static bool IsTerminalEvent(string eventType) =>
            eventType is "response.completed" or "response.failed" or "response.incomplete" or "error";

        private static string? GetEventErrorMessage(JsonObject evt) =>
            evt["response"]?["error"]?["message"]?.GetValue<string>()
            ?? evt["error"]?["message"]?.GetValue<string>()
            ?? evt["message"]?.GetValue<string>();

        private static string? GetEventErrorValue(JsonObject evt, string property) =>
            evt["response"]?["error"]?[property]?.GetValue<string>()
            ?? evt["error"]?[property]?.GetValue<string>();

        private static string? GetIncompleteReason(JsonObject? source) =>
            source?["incomplete_details"]?["reason"]?.GetValue<string>()
            ?? source?["response"]?["incomplete_details"]?["reason"]?.GetValue<string>();

        private static bool IsMappedIncompleteReason(string? reason) =>
            string.Equals(reason, "max_output_tokens", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "content_filter", StringComparison.OrdinalIgnoreCase);

        private static string ResolveFinishReason(string? incompleteReason, bool hasToolCalls)
        {
            if (string.Equals(incompleteReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
                return "length";
            if (string.Equals(incompleteReason, "content_filter", StringComparison.OrdinalIgnoreCase))
                return "content_filter";
            return hasToolCalls ? "tool_calls" : "stop";
        }

        private static JsonArray? GetContentFilters(JsonObject? source)
        {
            JsonNode? filters = source?["content_filters"] ?? source?["response"]?["content_filters"];
            return filters is JsonArray array && array.Count > 0
                ? (JsonArray)array.DeepClone()
                : null;
        }

        private static void AttachContentFilters(JsonObject choice, JsonArray? contentFilters)
        {
            if (contentFilters is null || contentFilters.Count == 0)
                return;

            choice["content_filters"] = contentFilters.DeepClone();

            // chat.completions 호환: blocked/filtered 항목의 content_filter_results를 평탄화해 붙인다.
            foreach (JsonNode? item in contentFilters)
            {
                if (item is not JsonObject filterObject)
                    continue;

                bool blocked = filterObject["blocked"]?.GetValue<bool>() ?? false;
                if (filterObject["content_filter_results"] is not JsonObject results)
                    continue;

                bool anyFiltered = false;
                foreach (KeyValuePair<string, JsonNode?> property in results)
                {
                    if (property.Value is JsonObject category
                        && (category["filtered"]?.GetValue<bool>() ?? false))
                    {
                        anyFiltered = true;
                        break;
                    }
                }

                if (blocked || anyFiltered)
                {
                    choice["content_filter_results"] = results.DeepClone();
                    break;
                }
            }
        }

        internal static string? SummarizeContentFilters(JsonArray? contentFilters)
        {
            if (contentFilters is null || contentFilters.Count == 0)
                return null;

            List<string> parts = new();
            foreach (JsonNode? item in contentFilters)
            {
                if (item is not JsonObject filterObject)
                    continue;

                string sourceType = filterObject["source_type"]?.GetValue<string>() ?? "unknown";
                bool blocked = filterObject["blocked"]?.GetValue<bool>() ?? false;

                if (filterObject["content_filter_results"] is JsonObject results)
                {
                    bool anyFiltered = false;
                    foreach (KeyValuePair<string, JsonNode?> property in results)
                    {
                        if (property.Value is not JsonObject category)
                            continue;

                        bool filtered = category["filtered"]?.GetValue<bool>() ?? false;
                        if (!filtered)
                            continue;

                        anyFiltered = true;
                        string severity = category["severity"]?.GetValue<string>() ?? "filtered";
                        parts.Add($"{sourceType}:{property.Key}:{severity}");
                    }

                    if (!anyFiltered && blocked)
                        parts.Add($"{sourceType}:blocked");
                }
                else if (blocked)
                {
                    parts.Add($"{sourceType}:blocked");
                }
            }

            return parts.Count > 0 ? string.Join(",", parts) : null;
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
