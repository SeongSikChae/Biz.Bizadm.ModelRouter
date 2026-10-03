namespace Biz.Bizadm.ModelRouter.Server.Middleware
{
    public sealed class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;

        public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            string method = context.Request.Method;
            string path = context.Request.Path + context.Request.QueryString;
            string? remoteIp = context.Connection.RemoteIpAddress?.ToString();
            string? userAgent = context.Request.Headers.UserAgent.ToString();
            string? forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();

            if (string.IsNullOrWhiteSpace(userAgent))
                userAgent = null;

            if (string.IsNullOrWhiteSpace(forwardedFor))
                forwardedFor = null;

            _logger.LogInformation(
                "Request {Method} {Path} from {RemoteIp} User-Agent={UserAgent} X-Forwarded-For={ForwardedFor}",
                method,
                path,
                remoteIp ?? "-",
                userAgent ?? "-",
                forwardedFor ?? "-");

            await _next(context);
        }
    }
}
