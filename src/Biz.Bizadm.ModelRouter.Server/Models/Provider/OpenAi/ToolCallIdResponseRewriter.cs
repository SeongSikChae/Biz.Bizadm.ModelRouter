using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biz.Bizadm.ModelRouter.Server.Models.Provider.OpenAi
{
    internal static class ToolCallIdResponseRewriter
    {
        public static async Task RewriteHttpContentAsync(
            HttpResponseMessage response,
            ToolCallIdMapper mapper,
            bool isChatCompletions,
            CancellationToken cancellationToken)
        {
            if (mapper.Count == 0)
                return;

            HttpContent original = response.Content;
            MediaTypeHeaderValue? contentType = original.Headers.ContentType;
            bool isEventStream = IsEventStream(contentType);

            if (isEventStream)
            {
                response.Content = new ToolCallIdSseContent(
                    original,
                    mapper,
                    isChatCompletions,
                    contentType);
                return;
            }

            byte[] bodyBytes = await original.ReadAsByteArrayAsync(cancellationToken);
            original.Dispose();

            byte[] rewritten = RewriteJsonBody(bodyBytes, mapper, isChatCompletions);
            ByteArrayContent content = new ByteArrayContent(rewritten);
            if (contentType is not null)
                content.Headers.ContentType = contentType;

            response.Content = content;
        }

        public static string? RewriteSseDataPayload(string dataPayload, ToolCallIdMapper mapper, bool isChatCompletions)
        {
            if (string.IsNullOrWhiteSpace(dataPayload))
                return null;

            if (string.Equals(dataPayload.Trim(), "[DONE]", StringComparison.Ordinal))
                return null;

            try
            {
                if (JsonNode.Parse(dataPayload) is not JsonObject root)
                    return null;

                bool changed = isChatCompletions
                    ? mapper.RewriteChatCompletionsStreamChunk(root)
                    : mapper.RewriteResponsesStreamChunk(root);

                return changed ? root.ToJsonString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static byte[] RewriteJsonBody(byte[] bodyBytes, ToolCallIdMapper mapper, bool isChatCompletions)
        {
            if (bodyBytes.Length == 0)
                return bodyBytes;

            try
            {
                if (JsonNode.Parse(bodyBytes) is not JsonObject root)
                    return bodyBytes;

                bool changed = isChatCompletions
                    ? mapper.RewriteChatCompletionsResponse(root)
                    : mapper.RewriteResponsesResponse(root);

                return changed ? Encoding.UTF8.GetBytes(root.ToJsonString()) : bodyBytes;
            }
            catch (JsonException)
            {
                return bodyBytes;
            }
        }

        private static bool IsEventStream(MediaTypeHeaderValue? contentType)
        {
            if (contentType is null)
                return false;

            return string.Equals(contentType.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ToolCallIdSseContent : HttpContent
        {
            private readonly HttpContent _inner;
            private readonly ToolCallIdMapper _mapper;
            private readonly bool _isChatCompletions;

            public ToolCallIdSseContent(
                HttpContent inner,
                ToolCallIdMapper mapper,
                bool isChatCompletions,
                MediaTypeHeaderValue? contentType)
            {
                _inner = inner;
                _mapper = mapper;
                _isChatCompletions = isChatCompletions;

                if (contentType is not null)
                    Headers.ContentType = contentType;
            }

            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
                => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(
                Stream stream,
                System.Net.TransportContext? context,
                CancellationToken cancellationToken)
            {
                await using Stream upstream = await _inner.ReadAsStreamAsync(cancellationToken);
                using StreamReader reader = new StreamReader(
                    upstream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);

                while (true)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                        break;

                    string outputLine = TransformLine(line);
                    byte[] bytes = Encoding.UTF8.GetBytes(outputLine + "\n");
                    await stream.WriteAsync(bytes, cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();

                base.Dispose(disposing);
            }

            private string TransformLine(string line)
            {
                const string dataPrefix = "data:";
                if (!line.StartsWith(dataPrefix, StringComparison.Ordinal))
                    return line;

                string payload = line[dataPrefix.Length..];
                if (payload.StartsWith(' '))
                    payload = payload[1..];

                string? rewritten = RewriteSseDataPayload(payload, _mapper, _isChatCompletions);
                if (rewritten is null)
                    return line;

                return dataPrefix + " " + rewritten;
            }
        }
    }
}
