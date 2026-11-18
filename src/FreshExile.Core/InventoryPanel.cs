namespace FreshExile.Core;

/// <summary>
/// Is the inventory open: the game draws it on the right, its ornate header ("Inventory" plaque between gold wings)
/// is matched against a reference taken from a real 1920x1080 frame. Everything is measured from the right edge
/// in screen heights, as the game scales its UI by height.
/// </summary>
public static class InventoryPanel
{
    /// <summary>Left edge of the inventory panel, as a share of the screen height from the right edge.</summary>
    public const double WidthOfHeight = 0.615;

    // header strip: wings and plaque, without the tab captions under it
    const double X0 = 0.444, X1 = 0.167, Y0 = 0.005, Y1 = 0.09;
    const int Pw = 64, Ph = 8;

    /// <summary>Mean brightness difference to the reference (0..255) below which the inventory counts as open.</summary>
    public const double MaxDiff = 22;

    /// <summary>Screen x (pixels) where the open inventory starts.</summary>
    public static int PanelLeft(int width, int height) => width - (int)Math.Round(height * WidthOfHeight);

    public static bool IsOpen(byte[] bgra, int width, int height, int stride) =>
        Diff(Patch(bgra, width, height, stride)) < MaxDiff;

    public static double Diff(byte[] patch) => TreeAligner.PatchDiff(patch, Reference);

    public static byte[] Patch(byte[] bgra, int width, int height, int stride)
    {
        int x0 = width - (int)(height * X0), x1 = width - (int)(height * X1);
        int y0 = (int)(height * Y0), y1 = (int)(height * Y1);
        var patch = new byte[Pw * Ph];
        if (x0 < 0 || x1 <= x0 || y1 <= y0) return patch;
        for (var py = 0; py < Ph; py++)
            for (var px = 0; px < Pw; px++)
            {
                int sum = 0, n = 0;
                for (var y = y0 + py * (y1 - y0) / Ph; y < y0 + (py + 1) * (y1 - y0) / Ph; y++)
                    for (var x = x0 + px * (x1 - x0) / Pw; x < x0 + (px + 1) * (x1 - x0) / Pw; x++)
                    {
                        var i = y * stride + x * 4;
                        sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
                        n++;
                    }
                patch[py * Pw + px] = (byte)(n == 0 ? 0 : sum / (3 * n));
            }
        return patch;
    }

    static readonly byte[] Reference = Convert.FromBase64String(ReferenceBase64);

    // Patch() of TestData/frames/closed-town-vendor.png (inventory open, 1920x1080)
    const string ReferenceBase64 =
        "GRgZIisuLzAzMzkvKCMcGBoWEQ8ODS8gHBkzQjcnKyoqLD9TOSYcIRIgFBUaGxwaGhkbJikqJiUlHRkbHBwaHUNJVn5fZEA3PTA9S1ZRPD8hFSsHHUBWKyRHWEI/T09WbE5IQFVWQytqU1ITFCAiLTtISU9LSkhDTktTPjs3NSt/iF8bIxYNDhETEg8hVF04XD9DPEddYldbOC8xLy5mTXOFk00nPEtwaXOQPyEiHiBBPxoIBgoLGBtDRFOFalZnHRoYGRwcICMpKCMiMRxOU0hgaF1PV0dmJxgXJUlUSFA+YFgoFhEpOk1faFNXSkVDKg0YGBkSFRgZGxYWISVZXzJRJAsaKC47TktJVV5qdXd/g3h2hIeHgYKHiIuMjZKTkouGgHlycWtpYmNjZGBcVU9MSEc/MDQoJSUhHjE8KCcTKDYlJzVFTE0/TFxjbnyGlZyEcGJ/XqFmh5ZNkHKFco2GZGh4fFs2R2pQUmxxZ11bW1tTSztvPzAwPCQePBoMHhgcKC4xL1FINkBHSk5TY293bXB6hn2NkYeNfHtwfHJ3dG55aWhsXWdaYF1mY1lUUk1IREE4iScqJSAUDhEREB0ZGBgXFRUaISglISw5MzkmNiQgHRseKCsvKC8vKyMxJzAwLSwtJyQgHBsbHiQnNTwuMCQiJioWExYVExYYGhs=";
}
