using System.Runtime.CompilerServices;
using System.Text;

namespace Biz.Bizadm.ModelRouter.Server.Models.Conversion
{
    public sealed record SseEvent(string? Event, string Data);

    /// <summary>
    /// SSE를 이벤트 단위로 읽는다. 줄 끝은 \n과 \r\n을 모두 받고, 여러 data: 줄은 \n으로 잇고, 빈 줄이 경계다.
    /// </summary>
    public static class SseReader
    {
        public static async IAsyncEnumerable<SseEvent> ReadAsync(
            Stream stream,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            string? eventName = null;
            StringBuilder? data = null;

            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;

                if (line.Length == 0)
                {
                    if (data is not null)
                        yield return new SseEvent(eventName, data.ToString());
                    eventName = null;
                    data = null;
                    continue;
                }

                if (line[0] == ':')
                    continue;

                int colon = line.IndexOf(':');
                string field = colon < 0 ? line : line[..colon];
                string value = colon < 0 ? string.Empty : line[(colon + 1)..];
                if (value.StartsWith(' '))
                    value = value[1..];

                switch (field)
                {
                    case "event":
                        eventName = value;
                        break;
                    case "data":
                        if (data is null)
                            data = new StringBuilder(value);
                        else
                            data.Append('\n').Append(value);
                        break;
                }
            }

            if (data is not null)
                yield return new SseEvent(eventName, data.ToString());
        }
    }
}
