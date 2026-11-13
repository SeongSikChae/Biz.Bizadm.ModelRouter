using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>오류 본문과 스트림 오류 이벤트를 상호 변환한다. 상태 코드는 유지한다(설계 4.4).</summary>
    public static class ErrorConverter
    {
        /// <summary>{type:error,error:{type,message}} → {error:{message,type,code:null}}</summary>
        public static byte[] AnthropicToOpenAi(byte[] body)
        {
            (string type, string message) = ReadAnthropicError(body);
            return Encoding.UTF8.GetBytes(OpenAiError(type, message).ToJsonString());
        }

        /// <summary>{error:{message,type,code}} → {type:error,error:{type(상태 코드 기준),message}}</summary>
        public static byte[] OpenAiToAnthropic(byte[] body, int statusCode)
        {
            string message = ReadOpenAiMessage(body);
            return Encoding.UTF8.GetBytes(AnthropicError(TypeForStatus(statusCode), message).ToJsonString());
        }

        /// <summary>Anthropic error 이벤트 data → OpenAI 오류 청크("data: …\n\n").</summary>
        public static string AnthropicToOpenAiStreamChunk(JsonObject errorEvent)
        {
            string type = errorEvent["error"]?["type"] is JsonValue t && t.TryGetValue(out string? typeText) ? typeText : "api_error";
            string message = errorEvent["error"]?["message"] is JsonValue m && m.TryGetValue(out string? messageText) ? messageText : "Upstream stream error.";
            return $"data: {OpenAiError(type, message).ToJsonString()}\n\n";
        }

        /// <summary>OpenAI 오류 청크({error:{…}}) → Anthropic error 이벤트. type은 청크의 code/type으로 정한다.</summary>
        public static string OpenAiToAnthropicStreamEvent(JsonObject errorChunk)
        {
            JsonNode? error = errorChunk["error"];
            string message = error?["message"] is JsonValue m && m.TryGetValue(out string? messageText) ? messageText : "Upstream stream error.";
            string? code = error?["code"]?.ToString();
            string? type = error?["type"] is JsonValue t && t.TryGetValue(out string? typeText) ? typeText : null;
            return AnthropicErrorEvent(TypeForStreamError(code, type), message);
        }

        public static string AnthropicErrorEvent(string type, string message)
            => $"event: error\ndata: {AnthropicError(type, message).ToJsonString()}\n\n";

        public static string OpenAiErrorChunk(string type, string message)
            => $"data: {OpenAiError(type, message).ToJsonString()}\n\n";

        internal static string TypeForStatus(int statusCode) => statusCode switch
        {
            400 => "invalid_request_error",
            401 => "authentication_error",
            403 => "permission_error",
            404 => "not_found_error",
            413 => "request_too_large",
            429 => "rate_limit_error",
            529 => "overloaded_error",
            >= 400 and < 500 => "invalid_request_error",
            _ => "api_error"
        };

        internal static string TypeForStreamError(string? code, string? type)
        {
            string text = $"{code} {type}".ToLowerInvariant();
            if (text.Contains("rate_limit") || text.Contains("429") || text.Contains("tpm"))
                return "rate_limit_error";
            if (text.Contains("overloaded"))
                return "overloaded_error";
            if (text.Contains("invalid_request_error"))
                return "invalid_request_error";
            return "api_error";
        }

        private static JsonObject OpenAiError(string type, string message)
            => new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = type, ["code"] = null } };

        private static JsonObject AnthropicError(string type, string message)
            => new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } };

        private static (string Type, string Message) ReadAnthropicError(byte[] body)
        {
            JsonNode? error = TryParse(body)?["error"];
            if (error?["message"] is JsonValue m && m.TryGetValue(out string? message))
            {
                string type = error["type"] is JsonValue t && t.TryGetValue(out string? typeText) ? typeText : "api_error";
                return (type, message);
            }

            return ("api_error", RawText(body));
        }

        private static string ReadOpenAiMessage(byte[] body)
        {
            JsonNode? root = TryParse(body);
            if (root?["error"]?["message"] is JsonValue m && m.TryGetValue(out string? message))
                return message;
            if (root?["error"] is JsonValue e && e.TryGetValue(out string? errorText))
                return errorText;
            return RawText(body);
        }

        private static JsonNode? TryParse(byte[] body)
        {
            try
            {
                return body.Length == 0 ? null : JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string RawText(byte[] body)
        {
            string text = Encoding.UTF8.GetString(body).Trim();
            return text.Length > 0 ? text : "Upstream returned an empty error body.";
        }
    }
}
