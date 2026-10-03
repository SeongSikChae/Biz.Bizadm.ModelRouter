using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry
{
    public sealed class AzureFoundryChatClient
    {
        private const string ChatCompletionsPath = "openai/v1/chat/completions";
        private const string ResponsesPath = "openai/v1/responses";
        private const string ModelsPath = "openai/v1/models";

        private readonly HttpClient _httpClient;
        private readonly Uri _chatCompletionsUri;
        private readonly Uri _responsesUri;
        private readonly Uri _modelsUri;
        private readonly string _endpoint;
        private readonly Dictionary<string, string> _reasoningEffortByModel;

        public AzureFoundryChatClient(HttpClient httpClient, Configuration configuration)
        {
            _httpClient = httpClient;

            _endpoint = configuration.OpenAIEndpoint.TrimEnd('/');
            _chatCompletionsUri = new Uri($"{_endpoint}/{ChatCompletionsPath}");
            _responsesUri = new Uri($"{_endpoint}/{ResponsesPath}");
            _modelsUri = new Uri($"{_endpoint}/{ModelsPath}");
            _reasoningEffortByModel = BuildReasoningEffortLookup(configuration.ReasoningEffortByModel);
        }

        public async Task<HttpResponseMessage> ForwardChatCompletionsAsync(
            Stream requestBody,
            string? contentType,
            CancellationToken cancellationToken)
        {
            using MemoryStream bufferedBody = new MemoryStream();
            await requestBody.CopyToAsync(bufferedBody, cancellationToken);
            bufferedBody.Position = 0;

            byte[] original = bufferedBody.ToArray();
            if (!TryParseObject(original, out JsonObject? rootObject) || rootObject is null)
                return await PostRawAsync(_chatCompletionsUri, original, contentType, cancellationToken);

            if (!HasFunctionTools(rootObject)
                || !TryGetModelName(rootObject, out string? model)
                || model is null
                || !_reasoningEffortByModel.TryGetValue(model, out string? effort))
            {
                return await PostRawAsync(_chatCompletionsUri, original, contentType, cancellationToken);
            }

            // none: chat/completions + tools는 reasoning_effort=none만 허용
            if (string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase))
            {
                byte[] payload = EnsureReasoningEffort(rootObject, original, effort);
                return await PostRawAsync(_chatCompletionsUri, payload, contentType, cancellationToken);
            }

            // none이 아니면 tools + reasoning을 Responses API로 우회 후 chat 형식으로 되돌림
            return await ForwardViaResponsesAsync(rootObject, effort, contentType, cancellationToken);
        }

        private async Task<HttpResponseMessage> ForwardViaResponsesAsync(
            JsonObject chatRoot,
            string reasoningEffort,
            string? contentType,
            CancellationToken cancellationToken)
        {
            string? modelHint = chatRoot["model"]?.GetValue<string>();
            bool stream = chatRoot["stream"] is JsonValue streamValue
                && streamValue.TryGetValue(out bool streamEnabled)
                && streamEnabled;

            byte[] responsesPayload = ChatResponsesBridge.ToResponsesRequest(chatRoot, reasoningEffort);

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
                    return new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
                    {
                        Content = new ByteArrayContent(responsesBody)
                        {
                            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                        }
                    };
                }

                byte[] chatBody = ChatResponsesBridge.ToChatCompletionResponse(responsesRoot);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(chatBody)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                    }
                };
            }

            Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            SseTransformContent transformed = new SseTransformContent(
                upstream,
                upstreamStream,
                modelHint,
                cancellationToken);

            HttpResponseMessage response = new HttpResponseMessage(upstream.StatusCode)
            {
                Content = transformed
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
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

        private async Task<HttpResponseMessage> PostStreamAsync(
            Uri uri,
            Stream requestBody,
            string? contentType,
            CancellationToken cancellationToken)
        {
            using MemoryStream bufferedBody = new MemoryStream();
            await requestBody.CopyToAsync(bufferedBody, cancellationToken);
            return await PostRawAsync(uri, bufferedBody.ToArray(), contentType, cancellationToken);
        }

        private static byte[] EnsureReasoningEffort(JsonObject rootObject, byte[] original, string effort)
        {
            JsonNode? reasoningEffort = rootObject["reasoning_effort"];
            if (reasoningEffort is JsonValue value
                && value.TryGetValue(out string? currentEffort)
                && string.Equals(currentEffort, effort, StringComparison.OrdinalIgnoreCase))
            {
                return original;
            }

            rootObject["reasoning_effort"] = effort;
            return Encoding.UTF8.GetBytes(rootObject.ToJsonString());
        }

        private static bool TryParseObject(byte[] payload, out JsonObject? rootObject)
        {
            rootObject = null;
            if (payload.Length == 0)
                return false;

            try
            {
                JsonNode? root = JsonNode.Parse(payload);
                if (root is not JsonObject obj)
                    return false;

                rootObject = obj;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static Dictionary<string, string> BuildReasoningEffortLookup(
            Dictionary<string, string>? configured)
        {
            Dictionary<string, string> lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (configured is null || configured.Count == 0)
                return lookup;

            foreach ((string model, string effort) in configured)
            {
                if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort))
                    continue;

                lookup[model.Trim()] = effort.Trim();
            }

            return lookup;
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

        public Task<HttpResponseMessage> ForwardResponsesAsync(
            Stream requestBody,
            string? contentType,
            CancellationToken cancellationToken)
        {
            return PostStreamAsync(_responsesUri, requestBody, contentType, cancellationToken);
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

        private async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            finally
            {
                request.Dispose();
            }
        }

        private sealed class SseTransformContent : HttpContent
        {
            private readonly HttpResponseMessage _upstreamResponse;
            private readonly Stream _upstreamStream;
            private readonly string? _modelHint;
            private readonly CancellationToken _cancellationToken;

            public SseTransformContent(
                HttpResponseMessage upstreamResponse,
                Stream upstreamStream,
                string? modelHint,
                CancellationToken cancellationToken)
            {
                _upstreamResponse = upstreamResponse;
                _upstreamStream = upstreamStream;
                _modelHint = modelHint;
                _cancellationToken = cancellationToken;
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
                                       _cancellationToken))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(chunk);
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
