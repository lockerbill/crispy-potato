using System.Net.WebSockets;
using System.Security;
using System.Text;
using System.Text.Json;
using FacelessStudio.Core.Models;

namespace FacelessStudio.Providers.Tts;

/// <summary>
/// Client WebSocket cho dịch vụ "Read aloud" của Microsoft Edge (giao thức theo edge-tts 7.3.1).
/// Trả về MP3 và vị trí từng chữ (WordBoundary).
/// </summary>
public sealed class EdgeTtsClient
{
    private const string BaseUrl = "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
    private const string ChromiumFullVersion = "143.0.3650.75";
    private const string ChromiumMajor = "143";
    private const long TicksPerSecond = 10_000_000;
    private const int Mp3BitrateBps = 48_000;
    private const int MaxChunkBytes = 4000;

    public sealed record Result(byte[] Audio, List<WordTiming> Words);

    public async Task<Result> SynthesizeAsync(string text, string voice, string rate, CancellationToken ct)
    {
        var audio = new MemoryStream();
        var words = new List<WordTiming>();
        long cumulativeBytes = 0;
        foreach (var chunk in SplitByBytes(CleanText(text), MaxChunkBytes))
        {
            var offsetTicks = cumulativeBytes * 8 * TicksPerSecond / Mp3BitrateBps;
            var before = audio.Length;
            await SynthesizeChunkAsync(chunk, voice, rate, audio, words, offsetTicks, ct);
            cumulativeBytes += audio.Length - before;
        }
        if (audio.Length == 0) throw new InvalidOperationException("Edge TTS không trả về âm thanh.");
        return new Result(audio.ToArray(), words);
    }

    private static async Task SynthesizeChunkAsync(string text, string voice, string rate, MemoryStream audio,
        List<WordTiming> words, long offsetTicks, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumMajor}.0.0.0 Safari/537.36 Edg/{ChromiumMajor}.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", $"muid={Guid.NewGuid().ToString("N").ToUpperInvariant()};");

        var url = $"wss://{BaseUrl}/edge/v1?TrustedClientToken={EdgeTtsToken.TrustedClientToken}" +
                  $"&ConnectionId={Guid.NewGuid():N}&Sec-MS-GEC={EdgeTtsToken.Generate()}" +
                  $"&Sec-MS-GEC-Version=2-{ChromiumFullVersion}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await ws.ConnectAsync(new Uri(url), timeout.Token);

        var date = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            System.Globalization.CultureInfo.InvariantCulture);
        await SendTextAsync(ws,
            $"X-Timestamp:{date}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\"," +
            "\"wordBoundaryEnabled\":\"true\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n",
            timeout.Token);

        var ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                   $"<voice name='{voice}'><prosody pitch='+0Hz' rate='{rate}' volume='+0%'>" +
                   SecurityElement.Escape(text) + "</prosody></voice></speak>";
        await SendTextAsync(ws,
            $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{date}Z\r\nPath:ssml\r\n\r\n{ssml}",
            timeout.Token);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            var (type, data) = await ReceiveAsync(ws, buffer, timeout.Token);
            if (type == WebSocketMessageType.Close)
                throw new InvalidOperationException("Edge TTS đóng kết nối trước khi xong.");
            if (type == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(data);
                var split = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var headers = split < 0 ? message : message[..split];
                var body = split < 0 ? "" : message[(split + 4)..];
                var path = HeaderValue(headers, "Path");
                if (path == "audio.metadata") ParseMetadata(body, offsetTicks, words);
                else if (path == "turn.end") break;
            }
            else if (data.Length >= 2)
            {
                var headerLength = data[0] << 8 | data[1];
                if (headerLength + 2 > data.Length) continue;
                var headers = Encoding.UTF8.GetString(data, 2, headerLength);
                if (HeaderValue(headers, "Path") != "audio") continue;
                audio.Write(data, headerLength + 2, data.Length - headerLength - 2);
            }
        }
        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Dịch vụ thường tự đóng trước, bỏ qua.
        }
    }

    private static void ParseMetadata(string json, long offsetTicks, List<WordTiming> words)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var meta in doc.RootElement.GetProperty("Metadata").EnumerateArray())
        {
            if (meta.GetProperty("Type").GetString() != "WordBoundary") continue;
            var data = meta.GetProperty("Data");
            var offset = data.GetProperty("Offset").GetInt64() + offsetTicks;
            var duration = data.GetProperty("Duration").GetInt64();
            var word = System.Net.WebUtility.HtmlDecode(data.GetProperty("text").GetProperty("Text").GetString() ?? "");
            words.Add(new WordTiming(word, offset / (double)TicksPerSecond, (offset + duration) / (double)TicksPerSecond));
        }
    }

    private static string? HeaderValue(string headers, string name)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            var idx = line.IndexOf(':');
            if (idx > 0 && line[..idx].EndsWith(name, StringComparison.OrdinalIgnoreCase)) return line[(idx + 1)..].Trim();
        }
        return null;
    }

    private static Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private static async Task<(WebSocketMessageType, byte[])> ReceiveAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
        return (result.MessageType, ms.ToArray());
    }

    /// <summary>Dịch vụ không nhận một số ký tự điều khiển (vd. vertical tab) nên thay bằng dấu cách.</summary>
    public static string CleanText(string text) =>
        new(text.Select(c => c is (>= '\0' and <= '\b') or '\v' or '\f' or (>= '\u000e' and <= '\u001f') ? ' ' : c).ToArray());

    /// <summary>Chia văn bản dài thành các đoạn không quá maxBytes (UTF-8), cắt ở dấu cách.</summary>
    public static IEnumerable<string> SplitByBytes(string text, int maxBytes)
    {
        var current = new StringBuilder();
        var currentBytes = 0;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetByteCount(word) + 1;
            if (currentBytes + bytes > maxBytes && current.Length > 0)
            {
                yield return current.ToString().Trim();
                current.Clear();
                currentBytes = 0;
            }
            current.Append(word).Append(' ');
            currentBytes += bytes;
        }
        if (current.Length > 0) yield return current.ToString().Trim();
    }
}
