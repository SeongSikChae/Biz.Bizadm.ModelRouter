using System.Diagnostics;

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
            var remoteIpAddress = context.Connection.RemoteIpAddress;
            string? remoteIp = remoteIpAddress is null
                ? null
                : (remoteIpAddress.IsIPv4MappedToIPv6 ? remoteIpAddress.MapToIPv4() : remoteIpAddress).ToString();
            string? userAgent = context.Request.Headers.UserAgent.ToString();
            string? forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();

            if (string.IsNullOrWhiteSpace(userAgent))
                userAgent = null;

            if (string.IsNullOrWhiteSpace(forwardedFor))
                forwardedFor = null;

            string requestId = context.TraceIdentifier;
            Stopwatch timer = Stopwatch.StartNew();
            using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId });
            context.Response.Headers["X-Request-Id"] = requestId;
            _logger.LogInformation(
                "Request {RequestId} {Method} {Path} from {RemoteIp} User-Agent={UserAgent} X-Forwarded-For={ForwardedFor}",
                requestId, method, path, remoteIp ?? "-", userAgent ?? "-", forwardedFor ?? "-");

            using CancellationTokenRegistration registration = context.RequestAborted.Register(() =>
                _logger.LogWarning(
                    "Request client disconnected RequestId={RequestId} ElapsedMs={ElapsedMs} ResponseStarted={ResponseStarted}",
                    requestId, timer.ElapsedMilliseconds, context.Response.HasStarted));
            try
            {
                await _next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Request cancelled RequestId={RequestId} ElapsedMs={ElapsedMs} ResponseStarted={ResponseStarted}",
                    requestId, timer.ElapsedMilliseconds, context.Response.HasStarted);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Request failed RequestId={RequestId} ElapsedMs={ElapsedMs} ResponseStarted={ResponseStarted} ClientAborted={ClientAborted}",
                    requestId,
                    timer.ElapsedMilliseconds,
                    context.Response.HasStarted,
                    context.RequestAborted.IsCancellationRequested);

                if (context.Response.HasStarted)
                    context.Abort();
                throw;
            }
            finally
            {
                _logger.LogInformation(
                    "Request ended RequestId={RequestId} StatusCode={StatusCode} ElapsedMs={ElapsedMs} ResponseStarted={ResponseStarted} ClientAborted={ClientAborted}",
                    requestId,
                    context.Response.StatusCode,
                    timer.ElapsedMilliseconds,
                    context.Response.HasStarted,
                    context.RequestAborted.IsCancellationRequested);
            }
        }
    }
}
