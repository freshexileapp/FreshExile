using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FreshExile.App;

/// <summary>
/// The screen as it was a moment ago, dimmed, over everything: drag a rectangle round the mercenary window, Esc to drop
/// it. The rectangle comes back in the capture's pixels.
/// </summary>
public sealed class RegionPickWindow : Window
{
    readonly int _pxWidth, _pxHeight;
    readonly Canvas _canvas = new();
    readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
    readonly Rectangle _frame = new() { StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly TaskCompletionSource<Int32Rect?> _done = new();
    Point? _from;
    Rect _sel;

    RegionPickWindow(byte[] screen, int width, int height, string hint)
    {
        _pxWidth = width;
        _pxHeight = height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Cursor = Cursors.Cross;
        _frame.Stroke = (Brush)Application.Current.Resources["Accent"];

        var shot = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, screen, width * 4);
        shot.Freeze();
        var image = new Image { Source = shot, Stretch = Stretch.Fill, Width = Width, Height = Height };
        _canvas.Children.Add(image);
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_frame);
        var tip = new Border
        {
            Background = (Brush)Application.Current.Resources["PanelBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BronzeLine"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = hint,
                Foreground = (Brush)Application.Current.Resources["Text"],
                FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
                FontSize = 14,
            },
        };
        tip.Loaded += (_, _) => Canvas.SetLeft(tip, (Width - tip.ActualWidth) / 2);
        Canvas.SetTop(tip, 24);
        _canvas.Children.Add(tip);
        Content = _canvas;
        Shade(Rect.Empty);

        MouseLeftButtonDown += (_, e) => { _from = e.GetPosition(_canvas); CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_from is not { } a) return;
            _sel = new Rect(a, e.GetPosition(_canvas));
            Shade(_sel);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            ReleaseMouseCapture();
            _from = null;
            if (_sel.Width < 40 || _sel.Height < 40) return;
            // the window is in device-independent units, the capture in pixels
            double kx = _pxWidth / ActualWidth, ky = _pxHeight / ActualHeight;
            Finish(new Int32Rect((int)(_sel.X * kx), (int)(_sel.Y * ky), (int)(_sel.Width * kx), (int)(_sel.Height * ky)));
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        Deactivated += (_, _) => Finish(null);
    }

    void Shade(Rect sel)
    {
        var all = new RectangleGeometry(new Rect(0, 0, Width, Height));
        _shade.Data = sel.IsEmpty ? all : new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(sel));
        if (sel.IsEmpty) return;
        _frame.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, sel.X);
        Canvas.SetTop(_frame, sel.Y);
        _frame.Width = sel.Width;
        _frame.Height = sel.Height;
    }

    void Finish(Int32Rect? r)
    {
        if (_done.Task.IsCompleted) return;
        _done.SetResult(r);
        Close();
    }

    /// <summary>The picked rectangle in screen pixels; null on Esc or when the window lost the focus.</summary>
    public static Task<Int32Rect?> PickAsync(byte[] screen, int width, int height, string hint)
    {
        var w = new RegionPickWindow(screen, width, height, hint);
        w.Show();
        w.Activate();
        return w._done.Task;
    }
}
