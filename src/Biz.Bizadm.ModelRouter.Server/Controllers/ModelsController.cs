using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    [ApiController]
    [Route("v1")]
    public sealed class ModelsController : ControllerBase
    {
        private readonly AzureFoundryChatClient _azureFoundryChatClient;

        public ModelsController(AzureFoundryChatClient azureFoundryChatClient)
        {
            _azureFoundryChatClient = azureFoundryChatClient;
        }

        [AllowAnonymous]
        [HttpGet("models")]
        public async Task ListModels(CancellationToken cancellationToken)
        {
            using HttpResponseMessage upstream = await _azureFoundryChatClient.ForwardModelsAsync(cancellationToken);
            await WriteUpstreamResponseAsync(upstream, cancellationToken);
        }

        [AllowAnonymous]
        [HttpGet("models/{model}")]
        public async Task GetModel(string model, CancellationToken cancellationToken)
        {
            using HttpResponseMessage upstream = await _azureFoundryChatClient.ForwardModelAsync(model, cancellationToken);
            await WriteUpstreamResponseAsync(upstream, cancellationToken);
        }

        private async Task WriteUpstreamResponseAsync(
            HttpResponseMessage upstream,
            CancellationToken cancellationToken)
        {
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            Response.StatusCode = (int)upstream.StatusCode;

            if (upstream.Content.Headers.ContentType is not null)
                Response.ContentType = upstream.Content.Headers.ContentType.ToString();

            await upstream.Content.CopyToAsync(Response.Body, cancellationToken);
        }
    }
}
