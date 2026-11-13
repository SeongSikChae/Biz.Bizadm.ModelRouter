using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    /// <summary>라우터가 직접 만드는 오류 응답을 입력 프로토콜 형식으로 쓴다(설계 3.1·4.4).</summary>
    internal static class RouteErrors
    {
        public static Task WriteModelNotFoundAsync(
            HttpResponse response,
            ApiProtocol inputProtocol,
            string? model,
            CancellationToken cancellationToken)
        {
            string message = string.IsNullOrWhiteSpace(model)
                ? "Request body must be a JSON object with a 'model' field."
                : $"The model '{model}' does not exist or is not configured on this router.";

            JsonObject body = inputProtocol == ApiProtocol.Anthropic
                ? AnthropicError("not_found_error", message)
                : OpenAiError("invalid_request_error", message, "model_not_found");

            return WriteAsync(response, StatusCodes.Status404NotFound, body, cancellationToken);
        }

        public static Task WriteInvalidRequestAsync(
            HttpResponse response,
            ApiProtocol inputProtocol,
            string message,
            CancellationToken cancellationToken)
        {
            JsonObject body = inputProtocol == ApiProtocol.Anthropic
                ? AnthropicError("invalid_request_error", message)
                : OpenAiError("invalid_request_error", message, null);

            return WriteAsync(response, StatusCodes.Status400BadRequest, body, cancellationToken);
        }

        internal static JsonObject AnthropicError(string type, string message)
            => new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = type, ["message"] = message }
            };

        internal static JsonObject OpenAiError(string type, string message, string? code)
            => new JsonObject
            {
                ["error"] = new JsonObject { ["message"] = message, ["type"] = type, ["code"] = code }
            };

        private static async Task WriteAsync(HttpResponse response, int statusCode, JsonObject body, CancellationToken cancellationToken)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            await response.WriteAsync(body.ToJsonString(), cancellationToken);
        }
    }
}
