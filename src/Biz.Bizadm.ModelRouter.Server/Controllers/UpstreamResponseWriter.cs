using Microsoft.AspNetCore.Http.Features;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    /// <summary>upstream 응답을 그대로 클라이언트에 복사한다(설계 3.3).</summary>
    internal static class UpstreamResponseWriter
    {
        private static readonly string[] AnthropicPassThroughHeaders = ["request-id"];

        public static async Task CopyAsync(
            HttpContext context,
            HttpResponseMessage upstream,
            CancellationToken cancellationToken,
            bool anthropicPassThrough = false)
        {
            WriteHead(context, upstream, anthropicPassThrough);

            if (upstream.Content.Headers.ContentType is not null)
                context.Response.ContentType = upstream.Content.Headers.ContentType.ToString();

            await upstream.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }

        /// <summary>버퍼링 해제, 상태 코드, 재시도·요청 식별 헤더를 설정한다. Content-Type과 본문은 호출자가 쓴다.</summary>
        public static void WriteHead(HttpContext context, HttpResponseMessage upstream, bool anthropicPassThrough)
        {
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.StatusCode = (int)upstream.StatusCode;

            CopyHeader(context, upstream, "Retry-After");

            if (!anthropicPassThrough)
                return;

            foreach (string name in AnthropicPassThroughHeaders)
                CopyHeader(context, upstream, name);

            foreach ((string name, IEnumerable<string> values) in upstream.Headers)
            {
                if (name.StartsWith("anthropic-ratelimit-", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[name] = values.ToArray();
            }
        }

        private static void CopyHeader(HttpContext context, HttpResponseMessage upstream, string name)
        {
            if (upstream.Headers.TryGetValues(name, out IEnumerable<string>? values))
                context.Response.Headers[name] = values.ToArray();
        }
    }
}
