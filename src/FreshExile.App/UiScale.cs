using System.Windows;
using System.Windows.Media;

namespace FreshExile.App;

/// <summary>
/// The interface's own zoom on top of Windows' DPI scaling (Settings → General): for a 1440p / 4K screen left at 100 %,
/// where everything comes out small. One shared transform on every window's content, so a change shows at once;
/// windows of a fixed size grow with it. The overlay on the game's tree is never scaled: it must sit on the game's nodes.
/// </summary>
public static class UiScale
{
    public static readonly double[] Steps = [0.9, 1.0, 1.15, 1.3, 1.5];

    public static readonly ScaleTransform Transform = new(1, 1);

    public static double Factor => Transform.ScaleX;

    /// <summary>The scale changed by this ratio (new / old): windows that size themselves follow.</summary>
    public static event Action<double>? Changed;

    public static void Set(double factor)
    {
        factor = Math.Clamp(factor, 0.75, 2.0);
        var old = Factor;
        if (Math.Abs(old - factor) < 0.001) return;
        Transform.ScaleX = Transform.ScaleY = factor;
        Changed?.Invoke(factor / old);
    }

    /// <param name="sizeNow">Grow the window's designed width (and a manual height) by the scale right away; false for a
    /// window that restores the size the user gave it (already in scaled pixels).</param>
    /// <param name="follow">Grow or shrink the window with each later change of the scale.</param>
    public static void Attach(Window w, bool sizeNow = true, bool follow = true)
    {
        if (w.Content is FrameworkElement root) root.LayoutTransform = Transform;
        if (sizeNow) Resize(w, Factor);
        if (!follow) return;
        Action<double> on = r => Resize(w, r);
        Changed += on;
        w.Closed += (_, _) => Changed -= on;
    }

    public static void Resize(Window w, double ratio)
    {
        if (Math.Abs(ratio - 1) < 0.001) return;
        if (w.SizeToContent is not (SizeToContent.Width or SizeToContent.WidthAndHeight) && !double.IsNaN(w.Width)) w.Width *= ratio;
        if (w.SizeToContent == SizeToContent.Manual && !double.IsNaN(w.Height)) w.Height *= ratio;
        if (w.MinWidth > 0) w.MinWidth *= ratio;
        if (w.MinHeight > 0) w.MinHeight *= ratio;
    }
}
