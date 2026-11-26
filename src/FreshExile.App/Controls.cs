using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FreshExile.App;

/// <summary>
/// "Коррапт  [любой · да · нет]": a three-way choice for a bool? filter (null = any, true = only such, false = none such),
/// the price check's corrupted / mirrored / fractured state. The look is in Theme.xaml.
/// </summary>
public sealed class TriSegment : Control
{
    static TriSegment() => DefaultStyleKeyProperty.OverrideMetadata(typeof(TriSegment), new FrameworkPropertyMetadata(typeof(TriSegment)));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(object), typeof(TriSegment));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(bool?), typeof(TriSegment),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TriSegment)d).Sync()));

    public object? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool? Value { get => (bool?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    ToggleButton? _any, _yes, _no;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _any = Hook("PART_Any", null);
        _yes = Hook("PART_Yes", true);
        _no = Hook("PART_No", false);
        Sync();
    }

    ToggleButton? Hook(string name, bool? value)
    {
        if (GetTemplateChild(name) is not ToggleButton b) return null;
        // a click always picks its own option (a second click does not switch it off)
        b.Click += (_, _) => { Value = value; Sync(); };
        return b;
    }

    void Sync()
    {
        if (_any != null) _any.IsChecked = Value == null;
        if (_yes != null) _yes.IsChecked = Value == true;
        if (_no != null) _no.IsChecked = Value == false;
    }
}

/// <summary>
/// A hint folded to its first sentence and "подробнее ›", which opens the rest (not remembered). The look is in Theme.xaml.
/// </summary>
public sealed class FoldHint : Control
{
    static FoldHint() => DefaultStyleKeyProperty.OverrideMetadata(typeof(FoldHint), new FrameworkPropertyMetadata(typeof(FoldHint)));

    public static readonly DependencyProperty ShortProperty = DependencyProperty.Register(nameof(Short), typeof(string), typeof(FoldHint));
    public static readonly DependencyProperty MoreProperty = DependencyProperty.Register(nameof(More), typeof(string), typeof(FoldHint));
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(FoldHint));

    /// <summary>The first sentence, always shown.</summary>
    public string? Short { get => (string?)GetValue(ShortProperty); set => SetValue(ShortProperty, value); }
    /// <summary>The rest, behind "подробнее ›".</summary>
    public string? More { get => (string?)GetValue(MoreProperty); set => SetValue(MoreProperty, value); }
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
}

/// <summary>Line · diamond · caption · diamond · line; with no caption one diamond in the middle. The look is in Theme.xaml.</summary>
public sealed class OrnamentDivider : Control
{
    static OrnamentDivider() => DefaultStyleKeyProperty.OverrideMetadata(typeof(OrnamentDivider), new FrameworkPropertyMetadata(typeof(OrnamentDivider)));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(OrnamentDivider), new PropertyMetadata(null, (d, _) =>
            ((OrnamentDivider)d).SetValue(HasTextPropertyKey, !string.IsNullOrEmpty(((OrnamentDivider)d).Text))));

    static readonly DependencyPropertyKey HasTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(HasText), typeof(bool), typeof(OrnamentDivider), new PropertyMetadata(false));

    public static readonly DependencyProperty HasTextProperty = HasTextPropertyKey.DependencyProperty;

    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool HasText => (bool)GetValue(HasTextProperty);
}

/// <summary>
/// The gems of one link stacked as rows, the link drawn under their sockets: a bar from the first shown row's socket to
/// the last one's (hidden rows, such as bought gems with "hide bought" on, are skipped). LinkX / LinkY: where the socket's
/// centre sits inside a row.
/// </summary>
public sealed class LinkedStackPanel : StackPanel
{
    public static readonly DependencyProperty LinkXProperty =
        DependencyProperty.Register(nameof(LinkX), typeof(double), typeof(LinkedStackPanel), new FrameworkPropertyMetadata(17.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LinkYProperty =
        DependencyProperty.Register(nameof(LinkY), typeof(double), typeof(LinkedStackPanel), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double LinkX { get => (double)GetValue(LinkXProperty); set => SetValue(LinkXProperty, value); }
    public double LinkY { get => (double)GetValue(LinkYProperty); set => SetValue(LinkYProperty, value); }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        InvalidateVisual();
        return size;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double? top = null, bottom = null;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Visible || child.RenderSize.Height < 1) continue;
            var y = child.TranslatePoint(new Point(0, LinkY), this).Y;
            top ??= y;
            bottom = y;
        }
        if (top is not double t || bottom is not double b || b - t < 1) return;
        var brush = TryFindResource("SocketLink") as Brush ?? Brushes.SaddleBrown;
        dc.DrawRoundedRectangle(brush, null, new Rect(LinkX - 2, t, 4, b - t), 2, 2);
    }
}

/// <summary>
/// A gem's socket: a 14 px ring in the socket colour, filled when the gem is bought (with a highlight), dark when not; a ring
/// of the card's own background around it keeps a link drawn under it from running into the circle.
/// </summary>
public sealed class SocketDot : FrameworkElement
{
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(nameof(Color), typeof(Brush), typeof(SocketDot),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledProperty =
        DependencyProperty.Register(nameof(Filled), typeof(bool), typeof(SocketDot),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The background the dot sits on (the gap ring around it).</summary>
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(Brush), typeof(SocketDot),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Color { get => (Brush)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public Brush? Gap { get => (Brush?)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    public double Diameter { get; set; } = 14;

    static readonly Brush Empty = Frozen(System.Windows.Media.Color.FromRgb(0x0C, 0x0A, 0x07));
    static readonly Brush Shine = MakeShine();

    protected override Size MeasureOverride(Size availableSize) => new(Diameter + 4, Diameter + 4);

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = Diameter / 2;
        if (Gap != null) dc.DrawEllipse(Gap, null, c, r + 2, r + 2);
        var color = Color;
        dc.DrawEllipse(Filled ? color : Empty, null, c, r, r);
        if (Filled) dc.DrawEllipse(Shine, null, c, r, r);
        var pen = new Pen(color, 2);
        pen.Freeze();
        dc.DrawEllipse(null, pen, c, r - 1, r - 1);
    }

    static Brush MakeShine()
    {
        var b = new RadialGradientBrush(System.Windows.Media.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF), Colors.Transparent)
        {
            GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.35, 0.3), RadiusX = 0.55, RadiusY = 0.55,
        };
        b.Freeze();
        return b;
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
