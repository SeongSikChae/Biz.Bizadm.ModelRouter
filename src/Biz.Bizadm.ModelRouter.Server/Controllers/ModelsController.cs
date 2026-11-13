using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Provider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    /// <summary>설정의 Providers[*].Models로 모델 목록을 응답한다. upstream을 호출하지 않는다(설계 D3·5절).</summary>
    [ApiController]
    [Route("v1")]
    public sealed class ModelsController : ControllerBase
    {
        private readonly ModelRouteResolver _resolver;

        public ModelsController(ModelRouteResolver resolver)
        {
            _resolver = resolver;
        }

        [AllowAnonymous]
        [HttpGet("models")]
        public Task ListModels(CancellationToken cancellationToken)
        {
            JsonArray data = new JsonArray();
            foreach (ModelRoute route in _resolver.Routes)
                data.Add(OpenAiModel(route));

            JsonObject body = new JsonObject
            {
                ["object"] = "list",
                ["data"] = data
            };
            return WriteJsonAsync(StatusCodes.Status200OK, body, cancellationToken);
        }

        [AllowAnonymous]
        [HttpGet("models/{model}")]
        public async Task GetModel(string model, CancellationToken cancellationToken)
        {
            ModelRoute? route = _resolver.FindExact(model);
            if (route is null)
            {
                await RouteErrors.WriteModelNotFoundAsync(Response, ApiProtocol.OpenAI, model, cancellationToken);
                return;
            }

            await WriteJsonAsync(StatusCodes.Status200OK, OpenAiModel(route), cancellationToken);
        }

        private static JsonObject OpenAiModel(ModelRoute route)
            => new JsonObject
            {
                ["id"] = route.Name,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = route.ProviderName
            };

        private async Task WriteJsonAsync(int statusCode, JsonObject body, CancellationToken cancellationToken)
        {
            Response.StatusCode = statusCode;
            Response.ContentType = "application/json";
            await Response.WriteAsync(body.ToJsonString(), cancellationToken);
        }
    }
}
