using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using WinForms = System.Windows.Forms;

namespace WinTool;

/// <summary>框选阶段的控制器：每个显示器一个覆盖窗口，选区用虚拟屏物理坐标表示。</summary>
internal static class SelectionSession
{
    static readonly List<SelectionOverlay> s_overlays = new();
    static ScreenShot? s_shot;
    static Action<Int32Rect?>? s_completed;
    static DrawingPoint s_start;
    static Int32Rect? s_selection;
    static bool s_committed;
    static bool s_sessionOpen;

    public static bool Dragging { get; private set; }
    public static Int32Rect? Selection => s_selection;
    /// <summary>已有框选会话（覆盖层还在）。覆盖层窗口本身缓存下来，平时隐藏。</summary>
    public static bool Active => s_sessionOpen;
    /// <summary>选区已确定、正在标注窗口中编辑；覆盖层保持压暗。</summary>
    public static bool Committed => s_committed;

    public static void Begin(ScreenShot shot, Action<Int32Rect?> completed)
    {
        Reset();
        s_shot = shot;
        s_completed = completed;
        s_sessionOpen = true;

        // 覆盖层窗口创建一次后缓存复用，之后每次截图只换图再显示，
        // 避免每次热键都重新创建 WPF 窗口（第一屏要等 100ms 以上）。
        EnsureOverlays();
        foreach (var overlay in s_overlays)
        {
            overlay.SetShot(shot);
            overlay.ShowAtPhysicalBounds();
        }

        if (s_overlays.Count > 0) s_overlays[^1].Activate();
    }

    static void EnsureOverlays()
    {
        var screens = WinForms.Screen.AllScreens;
        bool same = s_overlays.Count == screens.Length;
        if (same)
        {
            for (int i = 0; i < screens.Length; i++)
            {
                if (s_overlays[i].Monitor != screens[i].Bounds)
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            foreach (var overlay in s_overlays)
            {
                try { overlay.Close(); } catch { }
            }
            s_overlays.Clear();
        }

        if (s_overlays.Count == 0)
        {
            foreach (var screen in screens)
                s_overlays.Add(new SelectionOverlay(screen.Bounds));
        }
    }

    public static void PointerDown(SelectionOverlay origin)
    {
        Native.GetCursorPos(out var p);
        s_start = new DrawingPoint(p.X, p.Y);
        Dragging = true;
        s_selection = new Int32Rect(p.X, p.Y, 0, 0);
        origin.CaptureMouse();
        origin.Activate();
        foreach (var o in s_overlays) o.Redraw();
    }

    public static void PointerMove()
    {
        if (!Dragging) return;
        Native.GetCursorPos(out var p);
        s_selection = MakeRect(s_start.X, s_start.Y, p.X, p.Y);
        foreach (var o in s_overlays) o.Redraw();
    }

    public static void PointerUp()
    {
        if (!Dragging) return;
        Dragging = false;

        Native.GetCursorPos(out var p);
        s_selection = MakeRect(s_start.X, s_start.Y, p.X, p.Y);
        var sel = s_selection.Value;
        if (sel.Width < 2 || sel.Height < 2)
        {
            Cancel();
            return;
        }

        // 选区确定后覆盖层保留（压暗 + 选区高亮），交给标注窗口编辑，
        // 和常见截图工具一致；标注窗口关闭时再 Finish。
        // 必须先释放鼠标捕获，否则标注窗口收不到鼠标事件。
        foreach (var overlay in s_overlays)
        {
            try { overlay.ReleaseMouseCapture(); } catch { }
        }

        // 提交后 Dragging 已为 false，重绘一次让选区白框消失；
        // 亮区保留到标注窗口盖上为止，避免中间闪一帧全暗。
        s_committed = true;
        foreach (var overlay in s_overlays) overlay.Redraw();

        var completed = s_completed;
        completed?.Invoke(sel);
    }

    /// <summary>标注窗口关闭后收起覆盖层。</summary>
    public static void Finish() => Reset();

    /// <summary>点所在的覆盖窗，用作标注窗口的 Owner，保证标注窗口始终压在覆盖层之上。</summary>
    public static Window? OwnerAt(int x, int y)
    {
        foreach (var overlay in s_overlays)
            if (overlay.Contains(x, y)) return overlay;
        return s_overlays.Count > 0 ? s_overlays[0] : null;
    }

    public static void Cancel()
    {
        var completed = s_completed;
        Reset();
        completed?.Invoke(null);
    }

    static void Reset()
    {
        foreach (var overlay in s_overlays)
        {
            try { overlay.ReleaseMouseCapture(); } catch { }
            overlay.Hide();
        }
        s_shot = null;
        s_completed = null;
        s_selection = null;
        s_committed = false;
        s_sessionOpen = false;
        Dragging = false;
    }

    static Int32Rect MakeRect(int x1, int y1, int x2, int y2)
    {
        var shot = s_shot!;
        var b = shot.Bounds;
        int x = Math.Min(x1, x2);
        int y = Math.Min(y1, y2);
        int right = Math.Max(x1, x2);
        int bottom = Math.Max(y1, y2);

        x = Math.Max(x, b.X);
        y = Math.Max(y, b.Y);
        right = Math.Min(right, b.X + b.Width);
        bottom = Math.Min(bottom, b.Y + b.Height);

        return new Int32Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}

/// <summary>单个显示器上的框选覆盖层。冻结截图 + 半透明遮罩 + 选区高亮。</summary>
internal sealed class SelectionOverlay : Window
{
    readonly DrawingRectangle _monitor;
    readonly Path _mask;
    readonly Rectangle _border;
    readonly Border _labelBox;
    readonly TextBlock _label;
    readonly Canvas _canvas;
    readonly Image _image;

    public SelectionOverlay(DrawingRectangle monitor)
    {
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;

        _image = new Image { Stretch = Stretch.Fill };

        _mask = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        _border = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _label = new TextBlock { Foreground = Brushes.White, FontSize = 13 };
        _labelBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)),
            Padding = new Thickness(6, 2, 6, 2),
            Child = _label,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        _canvas = new Canvas();
        _canvas.Children.Add(_mask);
        _canvas.Children.Add(_border);
        _canvas.Children.Add(_labelBox);

        Content = new Grid { Children = { _image, _canvas } };

        SizeChanged += (_, _) => Redraw();
        MouseLeftButtonDown += (_, e) =>
        {
            if (!SelectionSession.Committed) SelectionSession.PointerDown(this);
            e.Handled = true;
        };
        MouseMove += (_, _) => { if (!SelectionSession.Committed && SelectionSession.Dragging) SelectionSession.PointerMove(); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!SelectionSession.Committed) SelectionSession.PointerUp();
            e.Handled = true;
        };
        // 右键取消要等这次右键消息处理完再执行：如果在按下时就隐藏覆盖层，
        // 松开的右键会落到下面的窗口上（比如桌面会弹出右键菜单）。
        MouseRightButtonDown += (_, e) => e.Handled = true;
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!SelectionSession.Committed)
                Dispatcher.BeginInvoke(new Action(SelectionSession.Cancel), DispatcherPriority.Background);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !SelectionSession.Committed)
            {
                SelectionSession.Cancel();
                e.Handled = true;
            }
        };
    }

    public DrawingRectangle Monitor => _monitor;

    public bool Contains(int x, int y) => _monitor.Contains(x, y);

    /// <summary>更新这一屏要显示的截图；窗口复用时每次会话调用一次。</summary>
    public void SetShot(ScreenShot shot)
    {
        var imageRect = new Int32Rect(
            _monitor.Left - shot.Bounds.X,
            _monitor.Top - shot.Bounds.Y,
            Math.Min(_monitor.Width, shot.Bounds.Width),
            Math.Min(_monitor.Height, shot.Bounds.Height));
        var crop = new CroppedBitmap(shot.Image, imageRect);
        crop.Freeze();
        _image.Source = crop;

        // 复用的窗口上残留着上次的选区遮罩，先清掉再显示。
        Redraw();
    }

    /// <summary>用物理像素直接定位，不做 DIP 换算。</summary>
    public void ShowAtPhysicalBounds()
    {
        // 先建好 HWND，在窗口可见之前就摆到正确位置和大小；
        // 直接 Show() 会先以 WPF 的默认尺寸（1440×759）在窗口默认位置闪一下。
        new WindowInteropHelper(this).EnsureHandle();
        ApplyBounds();
        Show();
        ApplyBounds();
        // 移动到不同 DPI 的显示器时 WPF 会按 WM_DPICHANGED 的建议矩形改窗口尺寸，
        // 所以在布局完成后再强制定位一次。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyBounds();
            UpdateLayout();
            Redraw();
        }), DispatcherPriority.Loaded);
    }

    void ApplyBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST,
            _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, Native.SWP_NOACTIVATE);
    }

    public void Redraw()
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        // 标注窗口铺满显示器、完全盖住覆盖层，亮区留着也不会露出来；
        // 提交时保留亮区，避免标注窗口出现前的这一帧整屏突然变暗（闪一下）。
        var selection = SelectionSession.Selection;
        Rect? hole = null;

        // 用窗口实际 DIP 尺寸和显示器物理尺寸的比值换算，不依赖全局 DPI 查询，
        // 保证遮罩孔与 Image 的 Stretch=Fill 完全对齐。
        double scaleX = w / _monitor.Width;
        double scaleY = h / _monitor.Height;

        if (selection is Int32Rect sel && sel.Width > 0 && sel.Height > 0)
        {
            int lx = Math.Max(sel.X, _monitor.Left);
            int ly = Math.Max(sel.Y, _monitor.Top);
            int rx = Math.Min(sel.X + sel.Width, _monitor.Right);
            int ry = Math.Min(sel.Y + sel.Height, _monitor.Bottom);
            if (rx > lx && ry > ly)
            {
                hole = new Rect(
                    (lx - _monitor.Left) * scaleX,
                    (ly - _monitor.Top) * scaleY,
                    (rx - lx) * scaleX,
                    (ry - ly) * scaleY);
            }
        }

        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, w, h)));
        if (hole is Rect hr) geometry.Children.Add(new RectangleGeometry(hr));
        _mask.Data = geometry;

        if (hole is Rect border && SelectionSession.Dragging)
        {
            _border.Visibility = Visibility.Visible;
            _border.Width = border.Width;
            _border.Height = border.Height;
            Canvas.SetLeft(_border, border.X);
            Canvas.SetTop(_border, border.Y);
        }
        else
        {
            _border.Visibility = Visibility.Collapsed;
        }

        if (selection is Int32Rect size && size.Width > 0 && size.Height > 0)
        {
            _label.Text = $"{size.Width} × {size.Height}";
            _labelBox.Visibility = Visibility.Visible;
            _labelBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lw = _labelBox.DesiredSize.Width;
            double lh = _labelBox.DesiredSize.Height;
            double left = hole?.X ?? 0;
            double top = hole?.Y ?? 0;
            left = Math.Clamp(left, 0, Math.Max(0, w - lw));
            top = top - lh - 4 >= 0 ? top - lh - 4 : top + 4;
            top = Math.Clamp(top, 0, Math.Max(0, h - lh));
            Canvas.SetLeft(_labelBox, left);
            Canvas.SetTop(_labelBox, top);
        }
        else
        {
            _labelBox.Visibility = Visibility.Collapsed;
        }
    }
}
