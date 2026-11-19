using System.Text.Json;

namespace FreshExile.Core;

/// <summary>A release on GitHub: its version and the archive to download.</summary>
public sealed record ReleaseInfo(Version Version, string ZipUrl, string PageUrl);

/// <summary>
/// The latest release of the public repository (GitHub's releases API, no token needed): newer than this build or not.
/// </summary>
public static class UpdateCheck
{
    public const string Repo = "freshexileapp/FreshExile";
    const string Api = $"https://api.github.com/repos/{Repo}/releases/latest";
    public const string Asset = "FreshExile.zip";

    /// <summary>GitHub's answer → the release, or null when it has no version tag or no <see cref="Asset"/>.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (ParseVersion(tag) is not { } version) return null;
        var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.Equals(name, Asset, StringComparison.OrdinalIgnoreCase) && url != null && url.StartsWith("https://", StringComparison.Ordinal))
                return new ReleaseInfo(version, url, page);
        }
        return null;
    }

    /// <summary>"v0.3.9" / "0.3.9" → 0.3.9 (three parts, the way the app shows its version).</summary>
    public static Version? ParseVersion(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? Three(v) : null;
    }

    public static Version Three(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > Three(current);

    public static async Task<ReleaseInfo?> LatestAsync(HttpClient http, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        return Parse(await res.Content.ReadAsStringAsync(ct));
    }
}
