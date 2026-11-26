using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FreshExile.App;

/// <summary>
/// Step number in a circle. Digits are centred by their ink bounds, not by the text line box
/// (the line box has room for ascenders/descenders, so layout-centred digits sit off-centre).
/// </summary>
public sealed class StepBadge : FrameworkElement
{
    public const double Size = 16;

    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Dictionary<string, Geometry> Glyphs = new();
    static readonly Brush Fill = Frozen(Color.FromRgb(0x0F, 0x0C, 0x09));
    static readonly Pen Ring = FrozenPen(Colors.White, 1);

    /// <summary>"Take this one next" / "this one" — stands out on both our dark tree and the gold in-game art.</summary>
    // the app's accent (Theme.xaml "Accent", the logo's flame): the next point to spend
    public static readonly Color FocusColor = Color.FromRgb(0xE8, 0xA4, 0x45);
    static readonly Brush FocusFill = Frozen(FocusColor);
    static readonly Pen FocusRing = FrozenPen(Color.FromRgb(0x0F, 0x0C, 0x09), 1);
    static readonly Brush FocusInk = Frozen(Color.FromRgb(0x0F, 0x0C, 0x09));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StepBadge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty IsFocusProperty = DependencyProperty.Register(
        nameof(IsFocus), typeof(bool), typeof(StepBadge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Filled with <see cref="FocusColor"/>: the next point to spend.</summary>
    public bool IsFocus
    {
        get => (bool)GetValue(IsFocusProperty);
        set => SetValue(IsFocusProperty, value);
    }

    protected override Size MeasureOverride(Size available) => new(WidthFor(Text), Size);

    protected override void OnRender(DrawingContext dc) =>
        Draw(dc, new Rect(0, 0, ActualWidth, ActualHeight), Text, IsFocus);

    public static double WidthFor(string text) => Math.Max(Size, Glyph(text).Bounds.Width + 9);

    /// <summary>Circle (capsule for 2+ digits) with the number optically centred.</summary>
    public static void Draw(DrawingContext dc, Rect box, string text, bool focus = false)
    {
        var r = box.Height / 2;
        dc.DrawRoundedRectangle(focus ? FocusFill : Fill, focus ? FocusRing : Ring, new Rect(box.X + 0.5, box.Y + 0.5, box.Width - 1, box.Height - 1), r - 0.5, r - 0.5);
        var g = Glyph(text);
        var b = g.Bounds;
        if (b.IsEmpty) return;
        dc.PushTransform(new TranslateTransform(box.X + (box.Width - b.Width) / 2 - b.X, box.Y + (box.Height - b.Height) / 2 - b.Y));
        dc.DrawGeometry(focus ? FocusInk : Brushes.White, null, g);
        dc.Pop();
    }

    static Geometry Glyph(string text)
    {
        if (Glyphs.TryGetValue(text, out var g)) return g;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, Brushes.White, 1.0);
        g = ft.BuildGeometry(new Point(0, 0));
        g.Freeze();
        return Glyphs[text] = g;
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Pen FrozenPen(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w);
        p.Freeze();
        return p;
    }
}
