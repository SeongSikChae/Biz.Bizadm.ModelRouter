using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider.Anthropic
{
    /// <summary>
    /// Anthropic Messages API(Anthropic 또는 Foundry Claude)로 요청을 전달한다.
    /// 본문은 model 별칭·effort 접미사가 있을 때만 다시 직렬화하고, 그 밖에는 바이트 그대로 보낸다.
    /// </summary>
    public sealed class AnthropicMessagesClient
    {
        private const string MessagesPath = "v1/messages";
        private const string CountTokensPath = "v1/messages/count_tokens";
        private const int MaxLoggedErrorBodyChars = 4096;

        private readonly HttpClient _httpClient;
        private readonly Configuration.ProviderConfiguration _provider;
        private readonly Uri _messagesUri;
        private readonly Uri _countTokensUri;
        private readonly ILogger<AnthropicMessagesClient> _logger;

        public AnthropicMessagesClient(
            HttpClient httpClient,
            Configuration.ProviderConfiguration provider,
            ILogger<AnthropicMessagesClient> logger)
        {
            _httpClient = httpClient;
            _provider = provider;
            _logger = logger;
            string endpoint = ProviderEndpoints.AnthropicBase(provider);
            _messagesUri = new Uri($"{endpoint}/{MessagesPath}");
            _countTokensUri = new Uri($"{endpoint}/{CountTokensPath}");
        }

        public Task<HttpResponseMessage> ForwardMessagesAsync(
            byte[] body,
            string? contentType,
            string? anthropicVersion,
            string? anthropicBeta,
            string upstreamModel,
            CancellationToken cancellationToken)
            => PostAsync(
                _messagesUri,
                RewriteModel(body, upstreamModel, applyEffort: true),
                contentType,
                anthropicVersion,
                anthropicBeta,
                cancellationToken);

        /// <summary>count_tokens는 접미사를 떼기만 하고 effort는 넣지 않는다(설계 D8).</summary>
        public Task<HttpResponseMessage> ForwardCountTokensAsync(
            byte[] body,
            string? contentType,
            string? anthropicVersion,
            string? anthropicBeta,
            string upstreamModel,
            CancellationToken cancellationToken)
            => PostAsync(
                _countTokensUri,
                RewriteModel(body, upstreamModel, applyEffort: false),
                contentType,
                anthropicVersion,
                anthropicBeta,
                cancellationToken);

        internal static byte[] RewriteModel(byte[] body, string upstreamModel, bool applyEffort)
        {
            JsonObject? root;
            try
            {
                root = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                return body;
            }

            if (root is null)
                return body;

            string? requestedModel = root["model"] is JsonValue value && value.TryGetValue(out string? model) ? model : null;
            AzureFoundryChatClient.TrySplitModelEffortSuffix(requestedModel, out _, out string? effort);
            bool modelChanged = !string.Equals(requestedModel, upstreamModel, StringComparison.Ordinal);
            if (effort is null && !modelChanged)
                return body;

            root["model"] = upstreamModel;
            if (applyEffort && effort is not null)
                AnthropicReasoning.ApplyEffort(root, effort);

            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }

        private async Task<HttpResponseMessage> PostAsync(
            Uri uri,
            byte[] payload,
            string? contentType,
            string? anthropicVersion,
            string? anthropicBeta,
            CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new ByteArrayContent(payload)
            };
            request.Content.Headers.TryAddWithoutValidation(
                "Content-Type",
                string.IsNullOrWhiteSpace(contentType) ? "application/json" : contentType);
            request.Headers.TryAddWithoutValidation(
                "anthropic-version",
                string.IsNullOrWhiteSpace(anthropicVersion) ? ProviderAuthentication.DefaultAnthropicVersion : anthropicVersion);
            if (!string.IsNullOrWhiteSpace(anthropicBeta))
                request.Headers.TryAddWithoutValidation("anthropic-beta", anthropicBeta);
            ProviderAuthentication.Apply(request, _provider, ApiProtocol.Anthropic);

            string path = uri.AbsolutePath;
            _logger.LogInformation(
                "Upstream send Method={Method} Path={Path} PayloadBytes={PayloadBytes}",
                request.Method,
                path,
                payload.Length);

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
                    await LogAndBufferErrorAsync(response, path, cancellationToken);

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
        }

        private async Task LogAndBufferErrorAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            string text = Encoding.UTF8.GetString(body).Trim();
            if (text.Length > MaxLoggedErrorBodyChars)
                text = text[..MaxLoggedErrorBodyChars] + "...(truncated)";

            _logger.LogWarning(
                "Upstream error Path={Path} StatusCode={StatusCode} Body={Body}",
                path,
                (int)response.StatusCode,
                text);

            ByteArrayContent buffered = new ByteArrayContent(body);
            foreach ((string name, IEnumerable<string> values) in response.Content.Headers)
                buffered.Headers.TryAddWithoutValidation(name, values);
            response.Content = buffered;
        }
    }
}
