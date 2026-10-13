using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider.OpenAi
{
    /// <summary>
    /// Request-scoped bidirectional map between client tool-call ids and short provider ids.
    /// </summary>
    public sealed class ToolCallIdMapper
    {
        private const string ProviderIdPrefix = "call_";
        private const int ProviderIdRandomBytes = 12;

        private readonly Dictionary<string, string> _toProvider = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _toClient = new Dictionary<string, string>(StringComparer.Ordinal);

        public int Count => _toProvider.Count;

        public string ToProvider(string clientId)
        {
            if (_toProvider.TryGetValue(clientId, out string? existing))
                return existing;

            string providerId;
            do
            {
                providerId = ProviderIdPrefix
                    + Convert.ToHexString(RandomNumberGenerator.GetBytes(ProviderIdRandomBytes))
                        .ToLowerInvariant();
            }
            while (_toClient.ContainsKey(providerId));

            _toProvider[clientId] = providerId;
            _toClient[providerId] = clientId;
            return providerId;
        }

        public string? TryToClient(string providerId)
        {
            return _toClient.TryGetValue(providerId, out string? clientId) ? clientId : null;
        }

        public bool RewriteChatCompletionsRequest(JsonObject root)
            => WalkToProvider(root);

        public bool RewriteResponsesRequest(JsonObject root)
            => WalkToProvider(root);

        public bool RewriteChatCompletionsResponse(JsonObject root)
            => WalkToClient(root);

        public bool RewriteResponsesResponse(JsonObject root)
            => WalkToClient(root);

        public bool RewriteChatCompletionsStreamChunk(JsonObject root)
            => WalkToClient(root);

        public bool RewriteResponsesStreamChunk(JsonObject root)
            => WalkToClient(root);

        private bool WalkToProvider(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    bool changed = false;
                    changed |= RewriteStringPropertyToProvider(obj, "tool_call_id");
                    changed |= RewriteStringPropertyToProvider(obj, "call_id");

                    if (obj["tool_calls"] is JsonArray toolCalls)
                    {
                        foreach (JsonNode? item in toolCalls)
                        {
                            if (item is JsonObject toolCall)
                                changed |= RewriteStringPropertyToProvider(toolCall, "id");

                            changed |= WalkToProvider(item);
                        }
                    }

                    foreach ((string key, JsonNode? value) in obj)
                    {
                        if (key is "tool_calls")
                            continue;

                        changed |= WalkToProvider(value);
                    }

                    return changed;
                }
                case JsonArray array:
                {
                    bool changed = false;
                    foreach (JsonNode? item in array)
                        changed |= WalkToProvider(item);
                    return changed;
                }
                default:
                    return false;
            }
        }

        private bool WalkToClient(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                {
                    bool changed = false;
                    changed |= RewriteStringPropertyToClient(obj, "tool_call_id");
                    changed |= RewriteStringPropertyToClient(obj, "call_id");

                    if (obj["tool_calls"] is JsonArray toolCalls)
                    {
                        foreach (JsonNode? item in toolCalls)
                        {
                            if (item is JsonObject toolCall)
                                changed |= RewriteStringPropertyToClient(toolCall, "id");

                            changed |= WalkToClient(item);
                        }
                    }

                    foreach ((string key, JsonNode? value) in obj)
                    {
                        if (key is "tool_calls")
                            continue;

                        changed |= WalkToClient(value);
                    }

                    return changed;
                }
                case JsonArray array:
                {
                    bool changed = false;
                    foreach (JsonNode? item in array)
                        changed |= WalkToClient(item);
                    return changed;
                }
                default:
                    return false;
            }
        }

        private bool RewriteStringPropertyToProvider(JsonObject obj, string propertyName)
        {
            if (!TryGetNonEmptyString(obj, propertyName, out string? clientId) || clientId is null)
                return false;

            string providerId = ToProvider(clientId);
            if (string.Equals(providerId, clientId, StringComparison.Ordinal))
                return false;

            obj[propertyName] = providerId;
            return true;
        }

        private bool RewriteStringPropertyToClient(JsonObject obj, string propertyName)
        {
            if (!TryGetNonEmptyString(obj, propertyName, out string? providerId) || providerId is null)
                return false;

            if (!_toClient.TryGetValue(providerId, out string? clientId))
                return false;

            if (string.Equals(providerId, clientId, StringComparison.Ordinal))
                return false;

            obj[propertyName] = clientId;
            return true;
        }

        private static bool TryGetNonEmptyString(JsonObject obj, string propertyName, out string? value)
        {
            value = null;
            if (obj[propertyName] is not JsonValue jsonValue)
                return false;

            if (!jsonValue.TryGetValue(out string? text) || string.IsNullOrEmpty(text))
                return false;

            value = text;
            return true;
        }
    }
}
