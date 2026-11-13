using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>변환기 공통 JSON 접근 헬퍼. 타입이 틀리면 ConversionException을 던진다.</summary>
    internal static class JsonNodes
    {
        public static string? GetString(JsonNode? node, string name)
        {
            JsonNode? value = node?[name];
            if (value is null)
                return null;
            if (value is JsonValue jsonValue && jsonValue.TryGetValue(out string? text))
                return text;
            throw new ConversionException($"'{name}' must be a string.");
        }

        public static bool GetBool(JsonNode? node, string name)
            => node?[name] is JsonValue value && value.TryGetValue(out bool flag) && flag;

        public static JsonArray RequireArray(JsonNode? node, string name)
            => node?[name] as JsonArray ?? throw new ConversionException($"'{name}' must be an array.");

        public static JsonObject RequireObject(JsonNode? node, string what)
            => node as JsonObject ?? throw new ConversionException($"{what} must be an object.");

        /// <summary>문자열이면 그대로, part/블록 배열이면 text 항목을 이어 붙인다. null이면 빈 문자열.</summary>
        public static string TextOf(JsonNode? content)
        {
            switch (content)
            {
                case null:
                    return string.Empty;
                case JsonValue value when value.TryGetValue(out string? text):
                    return text ?? string.Empty;
                case JsonArray parts:
                    StringBuilder builder = new StringBuilder();
                    foreach (JsonNode? part in parts)
                    {
                        if (part is JsonObject obj && GetString(obj, "type") == "text")
                            builder.Append(GetString(obj, "text"));
                    }
                    return builder.ToString();
                default:
                    throw new ConversionException("content must be a string or an array.");
            }
        }

        /// <summary>문자열이면 JSON으로 파싱해 객체를 돌려주고, 실패하거나 객체가 아니면 raw_arguments로 감싼다.</summary>
        public static JsonObject ParseArguments(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                return new JsonObject();

            try
            {
                if (JsonNode.Parse(arguments) is JsonObject obj)
                    return obj;
            }
            catch (JsonException)
            {
            }

            return new JsonObject { ["raw_arguments"] = arguments };
        }
    }
}
