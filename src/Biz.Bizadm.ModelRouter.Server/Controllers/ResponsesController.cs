using Biz.Bizadm.ModelRouter.Server.Models.Provider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    [ApiController]
    [Route("v1")]
    public sealed class ResponsesController : ControllerBase
    {
        private readonly ModelRouteResolver _resolver;
        private readonly ProviderClientRegistry _registry;

        public ResponsesController(ModelRouteResolver resolver, ProviderClientRegistry registry)
        {
            _resolver = resolver;
            _registry = registry;
        }

        [AllowAnonymous]
        [HttpPost("responses")]
        public async Task CreateResponse(CancellationToken cancellationToken)
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
                await RouteErrors.WriteInvalidRequestAsync(
                    Response,
                    ApiProtocol.OpenAI,
                    $"The model '{model}' uses the Anthropic API and is not supported on /v1/responses. Use /v1/chat/completions or /v1/messages.",
                    cancellationToken);
                return;
            }

            using HttpResponseMessage upstream = await _registry.GetOpenAi(route).ForwardResponsesAsync(
                new MemoryStream(body),
                Request.ContentType,
                cancellationToken,
                route.UpstreamModel);

            await UpstreamResponseWriter.CopyAsync(HttpContext, upstream, cancellationToken);
        }
    }
}
