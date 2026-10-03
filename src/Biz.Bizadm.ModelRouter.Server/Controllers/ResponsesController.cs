using Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Biz.Bizadm.ModelRouter.Server.Controllers
{
    [ApiController]
    [Route("v1")]
    public sealed class ResponsesController : ControllerBase
    {
        private readonly AzureFoundryChatClient _azureFoundryChatClient;

        public ResponsesController(AzureFoundryChatClient azureFoundryChatClient)
        {
            _azureFoundryChatClient = azureFoundryChatClient;
        }

        [AllowAnonymous]
        [HttpPost("responses")]
        public async Task CreateResponse(CancellationToken cancellationToken)
        {
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            using HttpResponseMessage upstream = await _azureFoundryChatClient.ForwardResponsesAsync(
                Request.Body,
                Request.ContentType,
                cancellationToken);

            Response.StatusCode = (int)upstream.StatusCode;

            if (upstream.Content.Headers.ContentType is not null)
                Response.ContentType = upstream.Content.Headers.ContentType.ToString();

            await upstream.Content.CopyToAsync(Response.Body, cancellationToken);
        }
    }
}
