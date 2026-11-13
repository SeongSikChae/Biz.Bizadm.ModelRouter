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

            if (route.Api != ApiProtocol.OpenAI)
            {
                // Anthropic 라우트 연결은 U8에서 한다.
                await RouteErrors.WriteInvalidRequestAsync(
                    Response,
                    ApiProtocol.OpenAI,
                    $"The model '{model}' is not available on /v1/chat/completions yet.",
                    cancellationToken);
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
    }
}
