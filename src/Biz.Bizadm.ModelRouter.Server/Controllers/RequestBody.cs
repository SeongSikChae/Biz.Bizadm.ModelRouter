using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    /// <summary>요청 본문 버퍼링과 model 추출.</summary>
    internal static class RequestBody
    {
        public static async Task<byte[]> ReadAllAsync(Stream body, CancellationToken cancellationToken)
        {
            using MemoryStream buffer = new MemoryStream();
            await body.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }

        /// <summary>본문이 JSON 객체이고 model이 비어 있지 않은 문자열이면 그 값을 반환한다.</summary>
        public static string? TryGetModel(byte[] body)
            => TryParseObject(body, out JsonObject? root) ? TryGetModel(root!) : null;

        public static string? TryGetModel(JsonObject root)
            => root["model"] is JsonValue value
                && value.TryGetValue(out string? model)
                && !string.IsNullOrWhiteSpace(model)
                    ? model
                    : null;

        public static bool TryParseObject(byte[] body, out JsonObject? root)
        {
            root = null;
            if (body.Length == 0)
                return false;

            try
            {
                root = JsonNode.Parse(body) as JsonObject;
                return root is not null;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
