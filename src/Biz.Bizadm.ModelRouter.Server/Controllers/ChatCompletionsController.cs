using System.Text;
using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Conversion;
using Biz.Bizadm.ModelRouter.Server.Models.Provider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    [ApiController]
    [Route("v1")]
    public sealed class ChatCompletionsController : ControllerBase
    {
        private readonly ModelRouteResolver _resolver;
        private readonly ProviderClientRegistry _registry;

        public ChatCompletionsController(ModelRouteResolver resolver, ProviderClientRegistry registry)
        {
            _resolver = resolver;
            _registry = registry;
        }

        [AllowAnonymous]
        [HttpPost("chat/completions")]
        public async Task ChatCompletions(CancellationToken cancellationToken)
        {
            byte[] body = await RequestBody.ReadAllAsync(Request.Body, cancellationToken);
            string? model = RequestBody.TryGetModel(body);
            ModelRoute? route = _resolver.Resolve(model);
            if (route is null)
            {
                await RouteErrors.WriteModelNotFoundAsync(Response, ApiProtocol.OpenAI, model, cancellationToken);
                return;
            }

            if (route.Api == ApiProtocol.Anthropic)
            {
                await ForwardToAnthropicAsync(body, model!, route, cancellationToken);
                return;
            }

            using HttpResponseMessage upstream = await _registry.GetOpenAi(route).ForwardChatCompletionsAsync(
                new MemoryStream(body),
                Request.ContentType,
                cancellationToken,
                HttpContext.TraceIdentifier,
                route.UpstreamModel);

            await UpstreamResponseWriter.CopyAsync(HttpContext, upstream, cancellationToken);
        }

        /// <summary>chat/completions → Anthropic messages 변환 경로 (설계 3.1).</summary>
        private async Task ForwardToAnthropicAsync(byte[] body, string model, ModelRoute route, CancellationToken cancellationToken)
        {
            // model을 읽었으므로 본문은 JSON 객체다.
            RequestBody.TryParseObject(body, out JsonObject? chat);
            AnthropicToolIdMapper ids = new AnthropicToolIdMapper();

            JsonObject message;
            try
            {
                message = OpenAiToAnthropicRequest.Convert(chat!, ids);
            }
            catch (ConversionException e)
            {
                await RouteErrors.WriteInvalidRequestAsync(Response, ApiProtocol.OpenAI, e.Message, cancellationToken);
                return;
            }

            bool includeUsage = chat!["stream_options"]?["include_usage"] is JsonValue value
                && value.TryGetValue(out bool enabled)
                && enabled;

            using HttpResponseMessage upstream = await _registry.GetAnthropic(route).ForwardMessagesAsync(
                Encoding.UTF8.GetBytes(message.ToJsonString()),
                "application/json",
                anthropicVersion: null,
                anthropicBeta: null,
                route.UpstreamModel,
                cancellationToken);

            await ConvertedResponseWriter.WriteOpenAiFromAnthropicAsync(HttpContext, upstream, model, includeUsage, ids, cancellationToken);
        }
    }
}
