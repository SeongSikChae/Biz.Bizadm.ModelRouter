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
            _reasoningEffortByModel = BuildReasoningEffortLookup(configuration.ReasoningEffortByModel);
        }

        public async Task<HttpResponseMessage> ForwardChatCompletionsAsync(
            Stream requestBody,
            string? contentType,
            CancellationToken cancellationToken)
        {
            using MemoryStream bufferedBody = new MemoryStream();
            await requestBody.CopyToAsync(bufferedBody, cancellationToken);
            byte[] original = bufferedBody.ToArray();
            byte[] payload = original;

            if (TryParseObject(original, out JsonObject? root) && root is not null)
            {
                // function tools + non-none reasoning_effort is rejected by Foundry on chat/completions
                if (HasFunctionTools(root))
                {
                    payload = ApplyChatReasoningEffort(root, "none", original);
                }
                else if (TryGetModelName(root, out string? model)
                    && model is not null
                    && _reasoningEffortByModel.TryGetValue(model, out string? effort)
                    && !string.IsNullOrWhiteSpace(effort))
                {
                    payload = ApplyChatReasoningEffort(root, effort, original);
                }
            }

            return await PostRawAsync(_chatCompletionsUri, payload, contentType, cancellationToken);
        }

        public Task<HttpResponseMessage> ForwardResponsesAsync(
            Stream requestBody,
            string? contentType,
            CancellationToken cancellationToken)
            => PostWithReasoningAsync(
                _responsesUri,
                requestBody,
                contentType,
                ApplyResponsesReasoningEffort,
                cancellationToken);

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

        private async Task<HttpResponseMessage> PostWithReasoningAsync(
            Uri uri,
            Stream requestBody,
            string? contentType,
            Func<JsonObject, string, byte[], byte[]> applyReasoning,
            CancellationToken cancellationToken)
        {
            using MemoryStream bufferedBody = new MemoryStream();
            await requestBody.CopyToAsync(bufferedBody, cancellationToken);
            byte[] original = bufferedBody.ToArray();
            byte[] payload = original;

            if (TryParseObject(original, out JsonObject? root)
                && root is not null
                && TryGetModelName(root, out string? model)
                && model is not null
                && _reasoningEffortByModel.TryGetValue(model, out string? effort)
                && !string.IsNullOrWhiteSpace(effort))
            {
                payload = applyReasoning(root, effort, original);
            }

            return await PostRawAsync(uri, payload, contentType, cancellationToken);
        }

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
    }
}
