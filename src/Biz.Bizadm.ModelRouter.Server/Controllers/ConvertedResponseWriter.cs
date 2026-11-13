using System.Text.Json;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Conversion;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    /// <summary>upstream 응답을 입력 프로토콜 형식으로 변환해 쓴다. 실패 → 오류 변환, SSE → 스트림 변환, 그 밖 → 응답 변환.</summary>
    internal static class ConvertedResponseWriter
    {
        public static async Task WriteAnthropicFromOpenAiAsync(
            HttpContext context,
            HttpResponseMessage upstream,
            string fallbackModel,
            CancellationToken cancellationToken)
        {
            UpstreamResponseWriter.WriteHead(context, upstream, anthropicPassThrough: false);
            HttpResponse response = context.Response;

            if (!upstream.IsSuccessStatusCode)
            {
                byte[] errorBody = await upstream.Content.ReadAsByteArrayAsync(cancellationToken);
                response.ContentType = "application/json";
                await response.Body.WriteAsync(ErrorConverter.OpenAiToAnthropic(errorBody, (int)upstream.StatusCode), cancellationToken);
                return;
            }

            // Anthropic 입력 → OpenAI 라우트: 기존 ToolCallIdMapper가 이미 원래 id로 복원하므로 항등 매퍼를 쓴다.
            AnthropicToolIdMapper identity = new AnthropicToolIdMapper();

            if (IsEventStream(upstream))
            {
                response.ContentType = "text/event-stream";
                await using Stream stream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
                await OpenAiToAnthropicStream.ConvertAsync(stream, response.Body, fallbackModel, identity, cancellationToken);
                return;
            }

            if (!TryReadObject(await upstream.Content.ReadAsByteArrayAsync(cancellationToken), out JsonObject? chat))
            {
                await RouteErrorsWriteAsync(response, RouteErrors.AnthropicError("api_error", "Upstream returned a non-JSON response."), cancellationToken);
                return;
            }

            response.ContentType = "application/json";
            await response.WriteAsync(OpenAiToAnthropicResponse.Convert(chat!, identity, fallbackModel).ToJsonString(), cancellationToken);
        }

        public static async Task WriteOpenAiFromAnthropicAsync(
            HttpContext context,
            HttpResponseMessage upstream,
            string fallbackModel,
            bool includeUsage,
            AnthropicToolIdMapper ids,
            CancellationToken cancellationToken)
        {
            UpstreamResponseWriter.WriteHead(context, upstream, anthropicPassThrough: false);
            HttpResponse response = context.Response;

            if (!upstream.IsSuccessStatusCode)
            {
                byte[] errorBody = await upstream.Content.ReadAsByteArrayAsync(cancellationToken);
                response.ContentType = "application/json";
                await response.Body.WriteAsync(ErrorConverter.AnthropicToOpenAi(errorBody), cancellationToken);
                return;
            }

            if (IsEventStream(upstream))
            {
                response.ContentType = "text/event-stream";
                await using Stream stream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
                await AnthropicToOpenAiStream.ConvertAsync(stream, response.Body, fallbackModel, includeUsage, ids, cancellationToken);
                return;
            }

            if (!TryReadObject(await upstream.Content.ReadAsByteArrayAsync(cancellationToken), out JsonObject? message))
            {
                await RouteErrorsWriteAsync(response, RouteErrors.OpenAiError("api_error", "Upstream returned a non-JSON response.", null), cancellationToken);
                return;
            }

            response.ContentType = "application/json";
            await response.WriteAsync(AnthropicToOpenAiResponse.Convert(message!, ids, fallbackModel).ToJsonString(), cancellationToken);
        }

        private static bool IsEventStream(HttpResponseMessage upstream)
            => string.Equals(upstream.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

        private static bool TryReadObject(byte[] body, out JsonObject? root)
        {
            try
            {
                root = body.Length == 0 ? null : JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                root = null;
            }
            return root is not null;
        }

        private static async Task RouteErrorsWriteAsync(HttpResponse response, JsonObject body, CancellationToken cancellationToken)
        {
            response.StatusCode = StatusCodes.Status502BadGateway;
            response.ContentType = "application/json";
            await response.WriteAsync(body.ToJsonString(), cancellationToken);
        }
    }
}
