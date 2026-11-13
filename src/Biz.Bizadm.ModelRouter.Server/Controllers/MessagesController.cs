using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Conversion;
using Biz.Bizadm.ModelRouter.Server.Models.Provider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    /// <summary>Anthropic Messages API 입력 (설계 3.1).</summary>
    [ApiController]
    [Route("v1")]
    public sealed class MessagesController : ControllerBase
    {
        private readonly ModelRouteResolver _resolver;
        private readonly ProviderClientRegistry _registry;

        public MessagesController(ModelRouteResolver resolver, ProviderClientRegistry registry)
        {
            _resolver = resolver;
            _registry = registry;
        }

        [AllowAnonymous]
        [HttpPost("messages")]
        public async Task Messages(CancellationToken cancellationToken)
        {
            byte[] body = await RequestBody.ReadAllAsync(Request.Body, cancellationToken);
            if (!RequestBody.TryParseObject(body, out JsonObject? root) || RequestBody.TryGetModel(root!) is not { } model)
            {
                await RouteErrors.WriteModelNotFoundAsync(Response, ApiProtocol.Anthropic, null, cancellationToken);
                return;
            }

            ModelRoute? route = _resolver.Resolve(model);
            if (route is null)
            {
                await RouteErrors.WriteModelNotFoundAsync(Response, ApiProtocol.Anthropic, model, cancellationToken);
                return;
            }

            if (route.Api == ApiProtocol.Anthropic)
            {
                using HttpResponseMessage passThrough = await _registry.GetAnthropic(route).ForwardMessagesAsync(
                    body,
                    Request.ContentType,
                    Request.Headers["anthropic-version"],
                    Request.Headers["anthropic-beta"],
                    route.UpstreamModel,
                    cancellationToken);
                await UpstreamResponseWriter.CopyAsync(HttpContext, passThrough, cancellationToken, anthropicPassThrough: true);
                return;
            }

            JsonObject chat;
            try
            {
                chat = AnthropicToOpenAiRequest.Convert(root!);
            }
            catch (ConversionException e)
            {
                await RouteErrors.WriteInvalidRequestAsync(Response, ApiProtocol.Anthropic, e.Message, cancellationToken);
                return;
            }

            using HttpResponseMessage upstream = await _registry.GetOpenAi(route).ForwardChatCompletionsAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(chat.ToJsonString())),
                "application/json",
                cancellationToken,
                HttpContext.TraceIdentifier,
                route.UpstreamModel);

            await ConvertedResponseWriter.WriteAnthropicFromOpenAiAsync(HttpContext, upstream, model, cancellationToken);
        }

        [AllowAnonymous]
        [HttpPost("messages/count_tokens")]
        public async Task CountTokens(CancellationToken cancellationToken)
        {
            byte[] body = await RequestBody.ReadAllAsync(Request.Body, cancellationToken);
            if (!RequestBody.TryParseObject(body, out JsonObject? root) || RequestBody.TryGetModel(root!) is not { } model)
            {
                await RouteErrors.WriteModelNotFoundAsync(Response, ApiProtocol.Anthropic, null, cancellationToken);
                return;
            }

            ModelRoute? route = _resolver.Resolve(model);
            if (route is null)
            {
                await RouteErrors.WriteModelNotFoundAsync(Response, ApiProtocol.Anthropic, model, cancellationToken);
                return;
            }

            if (route.Api == ApiProtocol.Anthropic)
            {
                using HttpResponseMessage passThrough = await _registry.GetAnthropic(route).ForwardCountTokensAsync(
                    body,
                    Request.ContentType,
                    Request.Headers["anthropic-version"],
                    Request.Headers["anthropic-beta"],
                    route.UpstreamModel,
                    cancellationToken);
                await UpstreamResponseWriter.CopyAsync(HttpContext, passThrough, cancellationToken, anthropicPassThrough: true);
                return;
            }

            int estimate;
            try
            {
                estimate = TokenEstimator.EstimateAnthropicInput(root!);
            }
            catch (ConversionException e)
            {
                await RouteErrors.WriteInvalidRequestAsync(Response, ApiProtocol.Anthropic, e.Message, cancellationToken);
                return;
            }

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/json";
            await Response.WriteAsync(new JsonObject { ["input_tokens"] = estimate }.ToJsonString(), cancellationToken);
        }
    }
}
