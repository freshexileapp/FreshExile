using System.IO.Compression;
using System.Text;

namespace FreshExile.Core;

public sealed class PobFormatException(string message) : Exception(message);

public static class PobCode
{
    public static bool IsUrl(string input) =>
        Uri.TryCreate(input.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

    public static Uri ToRawUrl(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return uri;
        return host switch
        {
            "pobb.in" or "www.pobb.in" when segs[^1] != "raw" => new Uri($"https://pobb.in/{segs[0]}/raw"),
            "pastebin.com" or "www.pastebin.com" when segs[0] != "raw" => new Uri($"https://pastebin.com/raw/{segs[0]}"),
            _ => uri,
        };
    }

    /// <summary>Link (pobb.in / pastebin) or a raw PoB code → the PoB code.</summary>
    public static async Task<string> ResolveCodeAsync(string input, HttpClient http, CancellationToken ct = default)
    {
        input = input.Trim();
        if (!IsUrl(input)) return input;
        var text = await http.GetStringAsync(ToRawUrl(new Uri(input)), ct);
        return text.Trim();
    }

    public static string DecodeToXml(string code)
    {
        var sb = new StringBuilder(code.Length + 3);
        foreach (var c in code)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }
        while (sb.Length % 4 != 0) sb.Append('=');

        byte[] bytes;
        try { bytes = Convert.FromBase64String(sb.ToString()); }
        catch (FormatException) { throw new PobFormatException(Loc.T("Это не код Path of Building")); }

        try
        {
            using var z = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch (InvalidDataException) { throw new PobFormatException(Loc.T("Код Path of Building повреждён")); }
    }
}
