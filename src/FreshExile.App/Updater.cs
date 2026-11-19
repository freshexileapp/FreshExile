using System.IO;
using System.IO.Compression;
using System.Net.Http;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>
/// Installs a newer release in two steps. While the app runs the archive is downloaded and its exe unpacked next to the
/// running one as FreshExile.exe.new. Only when the app exits is the running exe renamed to FreshExile.exe.old (Windows
/// lets a running exe be renamed, not overwritten) and the new one put in its place: a single-file exe loads assemblies
/// from its own file by path as they are first needed, so swapping it under a running app breaks the next such load.
/// An app that crashed instead of exiting swaps on its next start.
/// </summary>
public static class Updater
{
    static readonly HttpClient Download = CreateClient();

    static HttpClient CreateClient()
    {
        // the archive is ~60 MB: the app's 60-second client is too short for a slow line
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FreshExile");
        return http;
    }

    public static Version Current => UpdateCheck.Three(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    static string? Exe => Environment.ProcessPath;

    /// <summary>
    /// A build run from the sources (bin\Debug, bin\Release) has its assemblies beside the exe: it is never replaced by
    /// a release, only the single-file exe of the release archive is.
    /// </summary>
    public static bool CanInstall =>
        Exe is { } exe && Path.GetFileName(exe).Equals("FreshExile.exe", StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "FreshExile.Core.dll"));

    /// <summary>A downloaded version waits next to the exe for the app to exit.</summary>
    public static bool Pending => CanInstall && Exe is { } exe && LooksLikeExe(exe + ".new") && VersionOf(exe + ".new") > Current;

    /// <summary>The version an exe says it is ("0.3.9+abc…" → 0.3.9); null when it says none.</summary>
    static Version? VersionOf(string path)
    {
        try { return UpdateCheck.ParseVersion((System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "").Split('+')[0]); }
        catch (Exception) { return null; }
    }

    /// <summary>On start: the exe the last update replaced.</summary>
    public static void CleanUp()
    {
        if (Exe is not { } exe) return;
        try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); }
        catch (Exception) { /* the old version is still running: next start */ }
        // a download no newer than this exe (installed by hand meanwhile) is dropped
        try { if (File.Exists(exe + ".new") && !Pending) File.Delete(exe + ".new"); }
        catch (Exception) { /* next start */ }
    }

    /// <summary>
    /// Puts the downloaded version in place of the exe: on exit, or on a start after a crash. Uses nothing but the
    /// runtime's own file calls, so no assembly has to load from the exe while it is being swapped.
    /// </summary>
    public static bool ApplyPending()
    {
        if (!Pending || Exe is not { } exe) return false;
        try
        {
            File.Move(exe, exe + ".old", overwrite: true);
            try { File.Move(exe + ".new", exe); }
            catch (Exception)
            {
                File.Move(exe + ".old", exe, overwrite: true);
                return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Downloads the release and unpacks its exe next to the running one; the error text when it could not.</summary>
    public static async Task<string?> DownloadAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        if (!CanInstall || Exe is not { } exe) return Loc.T("сборка из исходников — обновляется сборкой");
        var zip = Path.Combine(StateStore.Dir, "update", UpdateCheck.Asset);
        var fresh = exe + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
            using (var res = await Download.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                await using var file = File.Create(zip + ".tmp");
                await res.Content.CopyToAsync(file, ct);
            }
            File.Move(zip + ".tmp", zip, overwrite: true);

            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("FreshExile.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return Loc.T("в архиве релиза нет FreshExile.exe");
                entry.ExtractToFile(fresh, overwrite: true);
            }
            if (!LooksLikeExe(fresh)) { File.Delete(fresh); return Loc.T("скачанный файл повреждён"); }
            Log.Write($"update: {release.Version} downloaded, installs on exit");
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return Loc.T("нет прав на запись в папку программы — скачай релиз вручную");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Log.Write($"update failed: {ex.GetType().Name} {ex.Message}");
            return Loc.T("не удалось скачать обновление");
        }
        finally
        {
            try { File.Delete(zip); } catch (Exception) { /* left for the next try */ }
        }
    }

    static bool LooksLikeExe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 5_000_000) return false;
            using var f = File.OpenRead(path);
            return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
        }
        catch (Exception) { return false; }
    }
}
