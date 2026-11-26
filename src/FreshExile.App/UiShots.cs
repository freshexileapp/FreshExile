using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FreshExile.App;

/// <summary>
/// QA (--ui-shots): every visible window drawn into a PNG by WPF itself, at 2x, on a dark backdrop; the panel once per tab
/// when a build is open.
/// </summary>
static class UiShots
{
    public static async void SaveAll(string dir, MainViewModel vm)
    {
        Directory.CreateDirectory(dir);
        foreach (Window w in Application.Current.Windows)
            if (w is not MainWindow)
            {
                ScrollToTop(w);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Save(w, Path.Combine(dir, w.GetType().Name + ".png"));
            }

        var main = Application.Current.MainWindow;
        if (main is not MainWindow mw) return;
        if (!vm.HasBuild || vm.IsCompact) { Save(main, Path.Combine(dir, "MainWindow.png")); return; }
        (string Name, Action Open)[] tabs =
        [
            ("Gems", () => vm.IsGemsTab = true), ("Tree", () => vm.IsTreeTab = true),
            ("Links", () => vm.IsLinksTab = true), ("Gear", () => vm.IsGearTab = true),
        ];
        foreach (var (name, open) in tabs)
        {
            open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Save(main, Path.Combine(dir, $"MainWindow-{name}.png"));
        }

        // a cluster jewel's card open on the gear tab, scrolled into view
        if (vm.GearJewels.FirstOrDefault(g => g.Slot == "Cluster") is { } cluster)
        {
            vm.IsGearTab = true;
            cluster.IsExpanded = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (FindByData(main, cluster) is { } card) card.BringIntoView();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Save(main, Path.Combine(dir, "MainWindow-Cluster.png"));
            cluster.IsExpanded = false;
        }
        vm.IsGemsTab = true;

        // the build switcher, open over the panel; the panel again after it closed (it must stay as it was)
        mw.OpenBuildPopupForShots();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (mw.BuildPopupChild is FrameworkElement pc) SaveElement(pc, Path.Combine(dir, "BuildPopup.png"));
        mw.CloseBuildPopupForShots();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Save(main, Path.Combine(dir, "MainWindow-AfterPopup.png"));

        mw.ShowTreeForShots();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        foreach (Window w in Application.Current.Windows)
            if (w is TreeWindow) Save(w, Path.Combine(dir, "TreeWindow.png"));

        // the strip over the game's tree (shown only while the game's tree is open): drawn here on its own
        var header = new TreeHeaderWindow(vm);
        header.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Save(header, Path.Combine(dir, "TreeHeaderWindow.png"));
        header.Close();
    }

    static FrameworkElement? FindByData(DependencyObject d, object data)
    {
        if (d is FrameworkElement fe && ReferenceEquals(fe.DataContext, data)) return fe;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindByData(VisualTreeHelper.GetChild(d, i), data) is { } found) return found;
        return null;
    }

    static void ScrollToTop(DependencyObject d)
    {
        if (d is System.Windows.Controls.ScrollViewer sv) sv.ScrollToHome();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) ScrollToTop(VisualTreeHelper.GetChild(d, i));
    }

    static void SaveElement(FrameworkElement e, string path)
    {
        const double scale = 2;
        int width = (int)Math.Ceiling(e.ActualWidth * scale), height = (int)Math.Ceiling(e.ActualHeight * scale);
        if (width < 2 || height < 2) return;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x17, 0x12)), null, new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            dc.DrawRectangle(new VisualBrush(e), null, new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        }
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    /// <summary>A window drawn far off the screen, never seen, then closed.</summary>
    public static void SaveOffScreen(Window w, string path)
    {
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = -20000;
        w.Show();
        w.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Save(w, path);
        w.Close();
    }

    static void Save(Window w, string path)
    {
        if (!w.IsVisible || w.ActualWidth < 1 || w.ActualHeight < 1) return;
        // a window faded out (the game is running and another one is in front) is drawn as it looks when shown
        var opacity = w.Opacity;
        w.Opacity = 1;
        w.UpdateLayout();
        // the window itself at 2x (margins and glows included), then laid on a dark backdrop
        const double scale = 2;
        int width = (int)Math.Ceiling(w.ActualWidth * scale), height = (int)Math.Ceiling(w.ActualHeight * scale);
        var shot = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        shot.Render(w);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x17, 0x12)), null, new Rect(0, 0, width, height));
            dc.DrawImage(shot, new Rect(0, 0, width, height));
        }
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
        w.Opacity = opacity;
    }
}
