using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreshExile.Core;

namespace FreshExile.App;

/// <summary>
/// The game's passive tree art from the export's sprite sheets: each sheet is downloaded once (kept on disk with the
/// item icons), its sprites cut out on first use. Asked for from rendering: a sheet not in yet answers null, starts its
/// download and raises <see cref="Arrived"/> when it is in, so the canvas draws again.
/// </summary>
public static class TreeArt
{
    static HttpClient? _http;
    static readonly Dictionary<string, BitmapSource?> Sheets = new();
    static readonly Dictionary<(string, string), ImageSource?> Cut = new();

    public static event Action? Arrived;

    public static void Init(HttpClient http) => _http = http;

    /// <summary>The sprite, or null while its sheet downloads (or when the sheet does not have it).</summary>
    public static ImageSource? Sprite(SpriteSheet sheet, string name)
    {
        if (!sheet.Coords.TryGetValue(name, out var r) || r.W <= 0 || r.H <= 0) return null;
        if (Cut.TryGetValue((sheet.Url, name), out var done)) return done;
        if (!Sheets.TryGetValue(sheet.Url, out var bmp))
        {
            Sheets[sheet.Url] = null;
            _ = LoadAsync(sheet.Url);
            return null;
        }
        if (bmp == null) return null;
        ImageSource? img = null;
        try
        {
            var rect = new Int32Rect(r.X, r.Y, Math.Min(r.W, bmp.PixelWidth - r.X), Math.Min(r.H, bmp.PixelHeight - r.Y));
            if (rect.Width > 0 && rect.Height > 0)
            {
                var c = new CroppedBitmap(bmp, rect);
                c.Freeze();
                img = c;
            }
        }
        catch (Exception) { /* a sheet that does not match its coords: the sprite stays out */ }
        Cut[(sheet.Url, name)] = img;
        return img;
    }

    static async Task LoadAsync(string url)
    {
        if (_http == null) return;
        var file = await ItemIcons.FileFor(_http, url);
        if (file == null)
        {
            // offline: asked again next time instead of never
            Sheets.Remove(url);
            return;
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            Sheets[url] = bmp;
            Arrived?.Invoke();
        }
        catch (Exception) { /* broken download: no art, the rings stay as they are */ }
    }
}
