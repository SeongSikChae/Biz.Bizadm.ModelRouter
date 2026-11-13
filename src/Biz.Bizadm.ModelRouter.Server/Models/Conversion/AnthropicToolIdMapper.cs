using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    /// <summary>
    /// 요청 단위 tool id 매핑. Anthropic tool_use id 허용 패턴(^[a-zA-Z0-9_-]+$) 밖의 id만 짧은 id로 바꾸고,
    /// 응답에서 원래 id로 되돌린다. 생성·역매핑 방식은 ToolCallIdMapper와 같다. 아무 것도 매핑하지 않으면 항등 매퍼다.
    /// </summary>
    public sealed partial class AnthropicToolIdMapper
    {
        private const string ProviderIdPrefix = "toolu_";
        private const int ProviderIdRandomBytes = 12;

        private readonly Dictionary<string, string> _toProvider = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _toClient = new(StringComparer.Ordinal);

        public int Count => _toProvider.Count;

        public string ToProvider(string clientId)
        {
            if (AllowedId().IsMatch(clientId))
                return clientId;

            if (_toProvider.TryGetValue(clientId, out string? existing))
                return existing;

            string providerId;
            do
            {
                providerId = ProviderIdPrefix
                    + Convert.ToHexString(RandomNumberGenerator.GetBytes(ProviderIdRandomBytes)).ToLowerInvariant();
            }
            while (_toClient.ContainsKey(providerId));

            _toProvider[clientId] = providerId;
            _toClient[providerId] = clientId;
            return providerId;
        }

        public string Restore(string providerId)
            => _toClient.TryGetValue(providerId, out string? clientId) ? clientId : providerId;

        [GeneratedRegex("^[a-zA-Z0-9_-]+$")]
        private static partial Regex AllowedId();
    }
}
