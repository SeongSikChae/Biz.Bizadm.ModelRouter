using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.OpenAi;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry
{
    public sealed class AzureFoundryChatClient
    {
        private const string ChatCompletionsPath = "openai/v1/chat/completions";
        private const string ResponsesPath = "openai/v1/responses";
        private const string ModelsPath = "openai/v1/models";
        private const int MaxLoggedErrorBodyChars = 4096;
        /// <summary>SupportsNone=false인 모델에서 Effort=none일 때 쓰는 최저 허용 effort.</summary>
        private const string LowestEffortWhenNoneUnsupported = "low";

        /// <summary>Bridge 등에서 Effort가 비어 있을 때 쓰는 기본값 (모델 기본과 동일).</summary>
        private const string DefaultEffortWhenMissing = "medium";

        private readonly HttpClient _httpClient;
        private readonly Uri _chatCompletionsUri;
        private readonly Uri _responsesUri;
        private readonly Uri _modelsUri;
        private readonly string _endpoint;
        private readonly Dictionary<string, ModelReasoningPolicy> _reasoningByModel;
        private readonly ILogger<AzureFoundryChatClient> _logger;

        public AzureFoundryChatClient(
            HttpClient httpClient,
            Configuration configuration,
            ILogger<AzureFoundryChatClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _endpoint = configuration.OpenAIEndpoint.TrimEnd('/');
            _chatCompletionsUri = new Uri($"{_endpoint}/{ChatCompletionsPath}");
            _responsesUri = new Uri($"{_endpoint}/{ResponsesPath}");
            _modelsUri = new Uri($"{_endpoint}/{ModelsPath}");
            _reasoningByModel = BuildReasoningLookup(configuration);
        }

        public async Task<HttpResponseMessage> ForwardChatCompletionsAsync(
            Stream requestBody,
            string? contentType,
            CancellationToken cancellationToken,
            string? requestId = null)
        {
            using MemoryStream bufferedBody = new MemoryStream();
            await requestBody.CopyToAsync(bufferedBody, cancellationToken);
            byte[] original = bufferedBody.ToArray();

            if (!TryParseObject(original, out JsonObject? root) || root is null)
                return await PostRawAsync(_chatCompletionsUri, original, contentType, cancellationToken);

            ToolCallIdMapper mapper = new ToolCallIdMapper();
            bool idsRewritten = mapper.RewriteChatCompletionsRequest(root);
            byte[] baseline = idsRewritten ? SerializeJsonObject(root) : original;

            TryGetModelName(root, out string? requestedModel);
            TrySplitModelEffortSuffix(requestedModel, out string? model, out string? suffixEffort);
            if (suffixEffort is not null)
            {
                root["model"] = model;
                baseline = SerializeJsonObject(root);
            }

            bool hasTools = HasFunctionTools(root);
            _reasoningByModel.TryGetValue(model ?? string.Empty, out ModelReasoningPolicy? policy);

            if (!hasTools)
            {
                string? effort = ResolveEffort(suffixEffort, policy?.Effort, policy?.SupportsNone ?? true);
                if (effort is not null)
                {
                    byte[] payload = ApplyChatReasoningEffort(root, effort, baseline);
                    return await PostChatAndRewriteAsync(payload, contentType, mapper, cancellationToken);
                }

                return await PostChatAndRewriteAsync(baseline, contentType, mapper, cancellationToken);
            }

            // tools 있음
            ChatToolsMode mode = policy?.ChatToolsMode ?? ChatToolsMode.PassThrough;
            string? configuredEffort = policy?.Effort;
            bool supportsNone = policy?.SupportsNone ?? true;

            if (mode == ChatToolsMode.ForceNone)
            {
                if (!supportsNone)
                {
                    string bridgeEffort = ResolveEffort(
                        suffixEffort,
                        configuredEffort,
                        supportsNone: false,
                        fallback: DefaultEffortWhenMissing)!;

                    _logger.LogInformation(
                        "ForceNone unsupported for model; bridging to Responses Model={Model} Effort={Effort}",
                        model,
                        bridgeEffort);

                    return await ForwardViaResponsesAsync(
                        root,
                        bridgeEffort,
                        contentType,
                        mapper,
                        cancellationToken,
                        requestId);
                }

                byte[] nonePayload = ApplyChatReasoningEffort(root, "none", baseline);
                return await PostChatAndRewriteAsync(nonePayload, contentType, mapper, cancellationToken);
            }

            if (mode == ChatToolsMode.BridgeToResponses)
            {
                string bridgeEffort = ResolveEffort(
                    suffixEffort,
                    configuredEffort,
                    supportsNone,
                    fallback: DefaultEffortWhenMissing)!;

                return await ForwardViaResponsesAsync(
                    root,
                    bridgeEffort,
                    contentType,
                    mapper,
                    cancellationToken,
                    requestId);
            }

            // PassThrough
            string? passThroughEffort = ResolveEffort(suffixEffort, configuredEffort, supportsNone);
            if (passThroughEffort is not null)
            {
                byte[] payload = ApplyChatReasoningEffort(root, passThroughEffort, baseline);
                return await PostChatAndRewriteAsync(payload, contentType, mapper, cancellationToken);
            }

            return await PostChatAndRewriteAsync(baseline, contentType, mapper, cancellationToken);
        }

        public async Task<HttpResponseMessage> ForwardResponsesAsync(
            Stream requestBody,
            string? contentType,
            CancellationToken cancellationToken)
        {
            using MemoryStream bufferedBody = new MemoryStream();
            await requestBody.CopyToAsync(bufferedBody, cancellationToken);
            byte[] original = bufferedBody.ToArray();

            if (!TryParseObject(original, out JsonObject? root) || root is null)
                return await PostRawAsync(_responsesUri, original, contentType, cancellationToken);

            ToolCallIdMapper mapper = new ToolCallIdMapper();
            bool idsRewritten = mapper.RewriteResponsesRequest(root);
            byte[] baseline = idsRewritten ? SerializeJsonObject(root) : original;
            byte[] payload = baseline;

            TryGetModelName(root, out string? requestedModel);
            TrySplitModelEffortSuffix(requestedModel, out string? model, out string? suffixEffort);
            if (suffixEffort is not null)
            {
                root["model"] = model;
                baseline = SerializeJsonObject(root);
                payload = baseline;
            }

            _reasoningByModel.TryGetValue(model ?? string.Empty, out ModelReasoningPolicy? policy);
            string? effort = ResolveEffort(suffixEffort, policy?.Effort, policy?.SupportsNone ?? true);
            if (effort is not null)
            {
                payload = ApplyResponsesReasoningEffort(root, effort, baseline);
            }

            HttpResponseMessage response = await PostRawAsync(
                _responsesUri,
                payload,
                contentType,
                cancellationToken);

            await ToolCallIdResponseRewriter.RewriteHttpContentAsync(
                response,
                mapper,
                isChatCompletions: false,
                cancellationToken);

            return response;
        }

        public Task<HttpResponseMessage> ForwardModelsAsync(CancellationToken cancellationToken)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, _modelsUri);
            return SendAsync(request, cancellationToken);
        }

        public Task<HttpResponseMessage> ForwardModelAsync(string model, CancellationToken cancellationToken)
        {
            Uri modelUri = new Uri($"{_endpoint}/{ModelsPath}/{Uri.EscapeDataString(model)}");
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, modelUri);
            return SendAsync(request, cancellationToken);
        }

        private async Task<HttpResponseMessage> ForwardViaResponsesAsync(
            JsonObject chatRoot,
            string reasoningEffort,
            string? contentType,
            ToolCallIdMapper mapper,
            CancellationToken cancellationToken,
            string? requestId)
        {
            string? modelHint = chatRoot["model"]?.GetValue<string>();
            bool stream = chatRoot["stream"] is JsonValue streamValue
                && streamValue.TryGetValue(out bool streamEnabled)
                && streamEnabled;
            bool includeUsage = stream
                && chatRoot["stream_options"]?["include_usage"] is JsonValue includeUsageValue
                && includeUsageValue.TryGetValue(out bool includeUsageEnabled)
                && includeUsageEnabled;

            byte[] responsesPayload = ChatResponsesBridge.ToResponsesRequest(chatRoot, reasoningEffort);

            _logger.LogInformation(
                "Bridging chat/completions to Responses Model={Model} Effort={Effort} Stream={Stream} PayloadBytes={PayloadBytes}",
                modelHint,
                reasoningEffort,
                stream,
                responsesPayload.Length);

            HttpResponseMessage upstream = await PostRawAsync(
                _responsesUri,
                responsesPayload,
                contentType,
                cancellationToken);

            if (!upstream.IsSuccessStatusCode)
                return upstream;

            if (!stream)
            {
                byte[] responsesBody = await upstream.Content.ReadAsByteArrayAsync(cancellationToken);
                upstream.Dispose();

                if (!TryParseObject(responsesBody, out JsonObject? responsesRoot) || responsesRoot is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadGateway)
                    {
                        Content = new ByteArrayContent(responsesBody)
                        {
                            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                        }
                    };
                }

                if (ChatResponsesBridge.TryCreateErrorResponse(responsesRoot, out byte[] upstreamErrorBody))
                {
                    _logger.LogWarning(
                        "Responses request returned terminal failure Model={Model} Status={Status}",
                        modelHint,
                        responsesRoot["status"]?.GetValue<string>());
                    return new HttpResponseMessage(HttpStatusCode.BadGateway)
                    {
                        Content = new ByteArrayContent(upstreamErrorBody)
                        {
                            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                        }
                    };
                }

                string? incompleteReason = responsesRoot["incomplete_details"]?["reason"]?.GetValue<string>();
                if (string.Equals(incompleteReason, "content_filter", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(incompleteReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase))
                {
                    string? filterSummary = ChatResponsesBridge.SummarizeContentFilters(
                        responsesRoot["content_filters"] as JsonArray);
                    if (!string.IsNullOrEmpty(filterSummary))
                    {
                        _logger.LogWarning(
                            "Responses incomplete mapped to finish_reason Model={Model} Reason={Reason} ContentFilters={ContentFilters}",
                            modelHint,
                            incompleteReason,
                            filterSummary);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Responses incomplete mapped to finish_reason Model={Model} Reason={Reason}",
                            modelHint,
                            incompleteReason);
                    }
                }

                byte[] chatBody = ChatResponsesBridge.ToChatCompletionResponse(responsesRoot, _logger, requestId);
                if (TryParseObject(chatBody, out JsonObject? chatRootObject)
                    && chatRootObject is not null
                    && mapper.RewriteChatCompletionsResponse(chatRootObject))
                {
                    chatBody = SerializeJsonObject(chatRootObject);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(chatBody)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                    }
                };
            }

            Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            ChatResponsesBridge.EarlyStreamPeekResult peek = await ChatResponsesBridge.PeekEarlyRateLimitFailureAsync(
                upstreamStream,
                _logger,
                cancellationToken);

            if (peek.RateLimitErrorBody is not null)
            {
                RetryConditionHeaderValue? retryAfter = upstream.Headers.RetryAfter;
                upstream.Dispose();
                _logger.LogWarning(
                    "Returning HTTP 429 for early Responses SSE rate-limit Model={Model}",
                    modelHint);

                HttpResponseMessage rateLimitResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new ByteArrayContent(peek.RateLimitErrorBody)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                    }
                };

                if (retryAfter is not null)
                    rateLimitResponse.Headers.RetryAfter = retryAfter;

                return rateLimitResponse;
            }

            SseTransformContent transformed = new SseTransformContent(
                upstream,
                peek.ContinueStream!,
                modelHint,
                includeUsage,
                mapper,
                _logger,
                cancellationToken,
                requestId);

            HttpResponseMessage response = new HttpResponseMessage(upstream.StatusCode)
            {
                Content = transformed
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        }

        private async Task<HttpResponseMessage> PostChatAndRewriteAsync(
            byte[] payload,
            string? contentType,
            ToolCallIdMapper mapper,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await PostRawAsync(
                _chatCompletionsUri,
                payload,
                contentType,
                cancellationToken);

            await ToolCallIdResponseRewriter.RewriteHttpContentAsync(
                response,
                mapper,
                isChatCompletions: true,
                cancellationToken);

            return response;
        }

        private static byte[] SerializeJsonObject(JsonObject root)
            => Encoding.UTF8.GetBytes(root.ToJsonString());

        private static byte[] ApplyChatReasoningEffort(JsonObject root, string effort, byte[] original)
        {
            if (root["reasoning_effort"] is JsonValue value
                && value.TryGetValue(out string? current)
                && string.Equals(current, effort, StringComparison.OrdinalIgnoreCase))
            {
                return original;
            }

            root["reasoning_effort"] = effort;
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }

        private static byte[] ApplyResponsesReasoningEffort(JsonObject root, string effort, byte[] original)
        {
            if (root["reasoning"] is JsonObject existingReasoning
                && existingReasoning["effort"] is JsonValue value
                && value.TryGetValue(out string? current)
                && string.Equals(current, effort, StringComparison.OrdinalIgnoreCase))
            {
                return original;
            }

            JsonObject reasoning = root["reasoning"] as JsonObject ?? new JsonObject();
            reasoning["effort"] = effort;
            root["reasoning"] = reasoning;
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }

        private async Task<HttpResponseMessage> PostRawAsync(
            Uri uri,
            byte[] payload,
            string? contentType,
            CancellationToken cancellationToken)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new ByteArrayContent(payload)
            };

            request.Content.Headers.TryAddWithoutValidation(
                "Content-Type",
                string.IsNullOrWhiteSpace(contentType) ? "application/json" : contentType);

            return await SendAsync(request, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            _logger.LogInformation(
                "Upstream send Method={Method} Path={Path} PayloadBytes={PayloadBytes}",
                request.Method,
                path,
                request.Content?.Headers.ContentLength);

            try
            {
                HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                _logger.LogInformation(
                    "Upstream headers Path={Path} StatusCode={StatusCode} ContentType={ContentType}",
                    path,
                    (int)response.StatusCode,
                    response.Content.Headers.ContentType);

                if (!response.IsSuccessStatusCode)
                    await LogAndBufferUpstreamErrorAsync(response, path, cancellationToken);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Upstream send failed Path={Path} Cancelled={Cancelled}",
                    path,
                    cancellationToken.IsCancellationRequested);
                throw;
            }
            finally
            {
                request.Dispose();
            }
        }

        private async Task LogAndBufferUpstreamErrorAsync(
            HttpResponseMessage response,
            string path,
            CancellationToken cancellationToken)
        {
            string? retryAfter = GetRetryAfter(response);
            string rateLimitHeaders = FormatRateLimitHeaders(response);
            string? msRequestId = GetFirstHeaderValue(
                response,
                "x-ms-request-id",
                "apim-request-id",
                "x-request-id");

            byte[] bodyBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            string errorBody = TruncateForLog(Encoding.UTF8.GetString(bodyBytes));
            TryParseUpstreamError(bodyBytes, out string? errorCode, out string? errorMessage, out string? errorType);

            _logger.LogWarning(
                "Upstream error Path={Path} StatusCode={StatusCode} RetryAfter={RetryAfter} " +
                "RateLimitHeaders={RateLimitHeaders} MsRequestId={MsRequestId} " +
                "ErrorCode={ErrorCode} ErrorType={ErrorType} ErrorMessage={ErrorMessage} ErrorBody={ErrorBody}",
                path,
                (int)response.StatusCode,
                retryAfter ?? "-",
                string.IsNullOrEmpty(rateLimitHeaders) ? "-" : rateLimitHeaders,
                msRequestId ?? "-",
                errorCode ?? "-",
                errorType ?? "-",
                errorMessage ?? "-",
                string.IsNullOrEmpty(errorBody) ? "-" : errorBody);

            MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
            response.Content.Dispose();
            response.Content = new ByteArrayContent(bodyBytes);
            if (contentType is not null)
                response.Content.Headers.ContentType = contentType;
        }

        private static string? GetRetryAfter(HttpResponseMessage response)
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                return ((int)Math.Ceiling(delta.TotalSeconds)).ToString();

            if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
                return date.ToString("O");

            if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
                return string.Join(",", values);

            return null;
        }

        private static string FormatRateLimitHeaders(HttpResponseMessage response)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            {
                if (!header.Key.StartsWith("x-ratelimit-", StringComparison.OrdinalIgnoreCase))
                    continue;

                parts.Add($"{header.Key}={string.Join(",", header.Value)}");
            }

            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {
                if (!header.Key.StartsWith("x-ratelimit-", StringComparison.OrdinalIgnoreCase))
                    continue;

                parts.Add($"{header.Key}={string.Join(",", header.Value)}");
            }

            return string.Join("; ", parts);
        }

        private static string? GetFirstHeaderValue(HttpResponseMessage response, params string[] names)
        {
            foreach (string name in names)
            {
                if (response.Headers.TryGetValues(name, out IEnumerable<string>? values))
                {
                    string? value = values.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return null;
        }

        private static void TryParseUpstreamError(
            byte[] bodyBytes,
            out string? errorCode,
            out string? errorMessage,
            out string? errorType)
        {
            errorCode = null;
            errorMessage = null;
            errorType = null;

            if (!TryParseObject(bodyBytes, out JsonObject? root) || root is null)
                return;

            if (root["error"] is not JsonObject error)
                return;

            errorCode = GetJsonString(error, "code");
            errorMessage = GetJsonString(error, "message");
            errorType = GetJsonString(error, "type");
        }

        private static string? GetJsonString(JsonObject obj, string propertyName)
        {
            if (obj[propertyName] is not JsonValue value)
                return null;

            if (value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                return text;

            return value.ToString();
        }

        private static string TruncateForLog(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            text = text.Trim();
            if (text.Length <= MaxLoggedErrorBodyChars)
                return text;

            return text[..MaxLoggedErrorBodyChars] + "...(truncated)";
        }

        private static Dictionary<string, ModelReasoningPolicy> BuildReasoningLookup(Configuration configuration)
        {
            Dictionary<string, ModelReasoningPolicy> lookup =
                new Dictionary<string, ModelReasoningPolicy>(StringComparer.OrdinalIgnoreCase);

            if (configuration.ReasoningEffortByModel is { Count: > 0 })
            {
                foreach ((string model, string effort) in configuration.ReasoningEffortByModel)
                {
                    if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort))
                        continue;

                    // 레거시 맵: tools가 있으면 BridgeToResponses (무조건 none 제거)
                    lookup[model.Trim()] = new ModelReasoningPolicy(
                        effort.Trim(),
                        ChatToolsMode.BridgeToResponses,
                        SupportsNone: true);
                }
            }

            if (configuration.ReasoningByModel is { Count: > 0 })
            {
                foreach ((string model, ModelReasoningOptions? options) in configuration.ReasoningByModel)
                {
                    if (string.IsNullOrWhiteSpace(model) || options is null)
                        continue;

                    string? effort = string.IsNullOrWhiteSpace(options.Effort) ? null : options.Effort.Trim();
                    ChatToolsMode mode = ParseChatToolsMode(options.ChatToolsMode);
                    bool supportsNone = options.SupportsNone ?? true;

                    lookup[model.Trim()] = new ModelReasoningPolicy(effort, mode, supportsNone);
                }
            }

            return lookup;
        }

        private static ChatToolsMode ParseChatToolsMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return ChatToolsMode.PassThrough;

            if (Enum.TryParse(value.Trim(), ignoreCase: true, out ChatToolsMode mode))
                return mode;

            return ChatToolsMode.PassThrough;
        }

        private static string? ResolveEffort(
            string? suffixEffort,
            string? configuredEffort,
            bool supportsNone,
            string? fallback = null)
        {
            string? effort = !string.IsNullOrWhiteSpace(suffixEffort)
                ? suffixEffort
                : !string.IsNullOrWhiteSpace(configuredEffort)
                    ? configuredEffort
                    : fallback;

            return string.IsNullOrWhiteSpace(effort)
                ? null
                : NormalizeEffort(effort, supportsNone);
        }

        private static bool TrySplitModelEffortSuffix(
            string? requestedModel,
            out string? baseModel,
            out string? effort)
        {
            baseModel = requestedModel;
            effort = null;

            if (string.IsNullOrWhiteSpace(requestedModel))
                return false;

            int separatorIndex = requestedModel.LastIndexOf('-');
            if (separatorIndex <= 0 || separatorIndex == requestedModel.Length - 1)
                return false;

            string suffix = requestedModel[(separatorIndex + 1)..];
            if (suffix.Equals("none", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("low", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("medium", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("high", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("xhigh", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("max", StringComparison.OrdinalIgnoreCase))
            {
                baseModel = requestedModel[..separatorIndex];
                effort = suffix.ToLowerInvariant();
                return true;
            }

            return false;
        }

        private static string NormalizeEffort(string effort, bool supportsNone)
        {
            if (!supportsNone
                && string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase))
            {
                return LowestEffortWhenNoneUnsupported;
            }

            return effort;
        }

        private static bool TryParseObject(byte[] payload, out JsonObject? rootObject)
        {
            rootObject = null;
            if (payload.Length == 0)
                return false;

            try
            {
                if (JsonNode.Parse(payload) is not JsonObject obj)
                    return false;

                rootObject = obj;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryGetModelName(JsonObject rootObject, out string? model)
        {
            model = null;
            if (rootObject["model"] is not JsonValue modelValue)
                return false;

            return modelValue.TryGetValue(out model) && !string.IsNullOrWhiteSpace(model);
        }

        private static bool HasFunctionTools(JsonObject rootObject)
        {
            if (rootObject["tools"] is not JsonArray tools || tools.Count == 0)
                return false;

            foreach (JsonNode? tool in tools)
            {
                if (tool is not JsonObject toolObject)
                    continue;

                if (toolObject["type"] is JsonValue typeValue
                    && typeValue.TryGetValue(out string? type)
                    && string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (toolObject.ContainsKey("function"))
                    return true;
            }

            return false;
        }

        private sealed record ModelReasoningPolicy(
            string? Effort,
            ChatToolsMode ChatToolsMode,
            bool SupportsNone);

        private sealed class SseTransformContent : HttpContent
        {
            private readonly HttpResponseMessage _upstreamResponse;
            private readonly Stream _upstreamStream;
            private readonly string? _modelHint;
            private readonly bool _includeUsage;
            private readonly ToolCallIdMapper _mapper;
            private readonly ILogger<AzureFoundryChatClient> _logger;
            private readonly CancellationToken _cancellationToken;
            private readonly string? _requestId;

            public SseTransformContent(
                HttpResponseMessage upstreamResponse,
                Stream upstreamStream,
                string? modelHint,
                bool includeUsage,
                ToolCallIdMapper mapper,
                ILogger<AzureFoundryChatClient> logger,
                CancellationToken cancellationToken,
                string? requestId)
            {
                _upstreamResponse = upstreamResponse;
                _upstreamStream = upstreamStream;
                _modelHint = modelHint;
                _includeUsage = includeUsage;
                _mapper = mapper;
                _logger = logger;
                _cancellationToken = cancellationToken;
                _requestId = requestId;
            }

            protected override async Task SerializeToStreamAsync(
                Stream stream,
                TransportContext? context)
            {
                try
                {
                    await foreach (string chunk in ChatResponsesBridge.ToChatCompletionSseAsync(
                                       _upstreamStream,
                                       _modelHint,
                                       _includeUsage,
                                       _logger,
                                       _cancellationToken,
                                       _requestId))
                    {
                        string output = ApplyMapperToBridgeSseEvent(chunk, _mapper);
                        byte[] bytes = Encoding.UTF8.GetBytes(output);
                        await stream.WriteAsync(bytes, _cancellationToken);
                        await stream.FlushAsync(_cancellationToken);
                    }
                }
                finally
                {
                    await _upstreamStream.DisposeAsync();
                    _upstreamResponse.Dispose();
                }
            }

            private static string ApplyMapperToBridgeSseEvent(string sseEvent, ToolCallIdMapper mapper)
            {
                if (mapper.Count == 0)
                    return sseEvent;

                const string prefix = "data: ";
                if (!sseEvent.StartsWith(prefix, StringComparison.Ordinal))
                    return sseEvent;

                string payload = sseEvent[prefix.Length..].TrimEnd('\n');
                string? rewritten = ToolCallIdResponseRewriter.RewriteSseDataPayload(
                    payload,
                    mapper,
                    isChatCompletions: true);

                if (rewritten is null)
                    return sseEvent;

                return prefix + rewritten + "\n\n";
            }

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _upstreamStream.Dispose();
                    _upstreamResponse.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
