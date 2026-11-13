using System.Text.Json.Nodes;
using Biz.Bizadm.ModelRouter.Server.Models.Provider;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    /// <summary>
    /// 설정의 Providers[*].Models로 모델 목록을 응답한다. upstream을 호출하지 않는다(설계 D3·5절).
    /// anthropic-version 헤더가 있으면 Anthropic 형식, 없으면 OpenAI 형식이다.
    /// </summary>
    [ApiController]
    [Route("v1")]
    public sealed class ModelsController : ControllerBase
    {
        private const string AnthropicCreatedAt = "1970-01-01T00:00:00Z";

        private readonly ModelRouteResolver _resolver;

        public ModelsController(ModelRouteResolver resolver)
        {
            _resolver = resolver;
        }

        private bool IsAnthropicRequest => Request.Headers.ContainsKey("anthropic-version");

        [AllowAnonymous]
        [HttpGet("models")]
        public Task ListModels(CancellationToken cancellationToken)
        {
            JsonArray data = new JsonArray();
            foreach (ModelRoute route in _resolver.Routes)
                data.Add(IsAnthropicRequest ? AnthropicModel(route) : OpenAiModel(route));

            JsonObject body = IsAnthropicRequest
                ? new JsonObject
                {
                    ["data"] = data,
                    ["has_more"] = false,
                    ["first_id"] = _resolver.Routes.Count > 0 ? _resolver.Routes[0].Name : null,
                    ["last_id"] = _resolver.Routes.Count > 0 ? _resolver.Routes[^1].Name : null
                }
                : new JsonObject
                {
                    ["object"] = "list",
                    ["data"] = data
                };
            return WriteJsonAsync(body, cancellationToken);
        }

        [AllowAnonymous]
        [HttpGet("models/{model}")]
        public async Task GetModel(string model, CancellationToken cancellationToken)
        {
            ModelRoute? route = _resolver.FindExact(model);
            if (route is null)
            {
                await RouteErrors.WriteModelNotFoundAsync(
                    Response,
                    IsAnthropicRequest ? ApiProtocol.Anthropic : ApiProtocol.OpenAI,
                    model,
                    cancellationToken);
                return;
            }

            await WriteJsonAsync(IsAnthropicRequest ? AnthropicModel(route) : OpenAiModel(route), cancellationToken);
        }

        private static JsonObject OpenAiModel(ModelRoute route)
            => new JsonObject
            {
                ["id"] = route.Name,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = route.ProviderName
            };

        private static JsonObject AnthropicModel(ModelRoute route)
            => new JsonObject
            {
                ["type"] = "model",
                ["id"] = route.Name,
                ["display_name"] = route.Name,
                ["created_at"] = AnthropicCreatedAt
            };

        private async Task WriteJsonAsync(JsonObject body, CancellationToken cancellationToken)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/json";
            await Response.WriteAsync(body.ToJsonString(), cancellationToken);
        }
    }
}
