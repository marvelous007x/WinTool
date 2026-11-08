using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace WinTool;

internal enum ToolKind { Rect, Ellipse, Arrow, Pen, Text }

internal sealed class Annotation
{
    public static readonly Color DefaultColor = Color.FromRgb(0xE8, 0x1D, 0x24);

    public ToolKind Kind { get; init; }
    public Color Color { get; init; } = DefaultColor;
    public Point Start { get; set; }
    public Point End { get; set; }
    public List<Point>? Points { get; set; }
    public string? Text { get; set; }
    public double FontSize { get; init; } = 16;
}

/// <summary>标注绘制层：live 预览和导出共用同一套绘制逻辑。</summary>
internal sealed class AnnotationLayer : FrameworkElement
{
    public List<Annotation> Items { get; } = new();
    public Annotation? Preview { get; set; }
    public double Thickness { get; set; } = 2.0;
    /// <summary>之后新建标注使用的颜色。</summary>
    public Color Color { get; set; } = Annotation.DefaultColor;

    protected override void OnRender(DrawingContext dc)
    {
        // 透明背景让整个层可命中鼠标
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        AnnotationRenderer.Draw(dc, Items, Thickness, pixelsPerDip);
        if (Preview is not null)
            AnnotationRenderer.Draw(dc, new[] { Preview }, Thickness, pixelsPerDip);
    }
}

/// <summary>选区边框 + 8 个缩放手柄。</summary>
internal sealed class SelectionFrame : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width;
        double h = RenderSize.Height;
        if (w < 2 || h < 2) return;

        dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(0.5, 0.5, w - 1, h - 1));

        const double size = 7;
        double x1 = 4, y1 = 4, x2 = w - 4, y2 = h - 4;
        if (x2 <= x1) x1 = x2 = w / 2;
        if (y2 <= y1) y1 = y2 = h / 2;

        var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1);
        double[] xs = { x1, (x1 + x2) / 2, x2 };
        double[] ys = { y1, (y1 + y2) / 2, y2 };
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (i == 1 && j == 1) continue;
                dc.DrawRectangle(Brushes.White, edge,
                    new Rect(xs[i] - size / 2, ys[j] - size / 2, size, size));
            }
        }
    }
}

internal static class AnnotationRenderer
{
    static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();

    static SolidColorBrush BrushFor(Color color)
    {
        if (!BrushCache.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();
            BrushCache[color] = brush;
        }
        return brush;
    }

    static Pen MakePen(Color color, double thickness) => new(BrushFor(color), thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    public static void Draw(DrawingContext dc, IEnumerable<Annotation> items, double thickness, double pixelsPerDip)
    {
        foreach (var item in items)
            DrawOne(dc, item, MakePen(item.Color, thickness), pixelsPerDip);
    }

    static void DrawOne(DrawingContext dc, Annotation a, Pen pen, double pixelsPerDip)
    {
        var rect = new Rect(a.Start, a.End); // 自动归一化
        switch (a.Kind)
        {
            case ToolKind.Rect:
                dc.DrawRectangle(null, pen, rect);
                break;

            case ToolKind.Ellipse:
                dc.DrawEllipse(null, pen,
                    new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                    rect.Width / 2, rect.Height / 2);
                break;

            case ToolKind.Arrow:
                DrawArrow(dc, a.Start, a.End, pen);
                break;

            case ToolKind.Pen:
                if (a.Points is { Count: > 1 })
                {
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        ctx.BeginFigure(a.Points[0], false, false);
                        ctx.PolyLineTo(a.Points.GetRange(1, a.Points.Count - 1), true, false);
                    }
                    geometry.Freeze();
                    dc.DrawGeometry(null, pen, geometry);
                }
                break;

            case ToolKind.Text:
                if (!string.IsNullOrEmpty(a.Text))
                {
                    var text = new FormattedText(
                        a.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface("Microsoft YaHei UI"), a.FontSize, BrushFor(a.Color), pixelsPerDip);
                    dc.DrawText(text, a.Start);
                }
                break;
        }
    }

    static void DrawArrow(DrawingContext dc, Point start, Point end, Pen pen)
    {
        dc.DrawLine(pen, start, end);
        var delta = end - start;
        double length = delta.Length;
        if (length < 2) return;

        var dir = delta / length;
        var perp = new Vector(-dir.Y, dir.X);
        double head = Math.Min(18, length * 0.5);
        var p1 = end - dir * head + perp * (head * 0.45);
        var p2 = end - dir * head - perp * (head * 0.45);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(end, true, true);
            ctx.LineTo(p1, true, false);
            ctx.LineTo(p2, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(pen.Brush, null, geometry);
    }
}

/// <summary>
/// 选区标注窗口：覆盖选区（选区太窄时窗口加宽放工具栏），
/// 覆盖层保持压暗；可拖手柄缩放选区、方向键微调位置，
/// 双击/回车复制并关闭，右键/Esc 取消。
/// </summary>
internal sealed class AnnotationWindow : Window
{
    const double ToolbarHeight = 44;
    const double EdgeHit = 7;

    readonly ScreenShot _shot;
    readonly double _scale;
    readonly System.Drawing.Rectangle _monitor;

    readonly Canvas _imageCanvas = new();
    readonly Grid _imageHost = new();
    readonly Canvas _overlay = new();
    readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    readonly System.Windows.Shapes.Path _mask = new() { IsHitTestVisible = false };
    readonly SelectionFrame _frame = new();
    readonly AnnotationLayer _layer = new();
    readonly List<(Button Button, ToolKind Kind)> _toolButtons = new();
    readonly System.Windows.Shapes.Rectangle _colorSwatch = new()
    {
        Width = 12,
        Height = 12,
        Stroke = Brushes.White,
        StrokeThickness = 1,
        Fill = new SolidColorBrush(Annotation.DefaultColor),
    };
    Button _colorButton = null!;
    Popup? _colorPopup;
    readonly List<(Color Color, Border Swatch)> _paletteSwatches = new();
    Border _toolbar = null!;
    double _toolbarWidthDip;

    Int32Rect _rect;

    ToolKind _tool;
    Point _dragStart;
    Annotation? _preview;
    bool _dragging;
    TextBox? _textBox;

    bool _resizing;
    int _resizeEdges;
    Native.POINT _resizeStartCursor;
    Int32Rect _resizeStartRect;

    public AnnotationWindow(ScreenShot shot, Int32Rect screenRect, App app)
    {
        _shot = shot;
        _rect = screenRect;
        _scale = Native.GetScaleForPoint(screenRect.X + screenRect.Width / 2, screenRect.Y + screenRect.Height / 2);
        _monitor = WinForms.Screen.FromPoint(new System.Drawing.Point(
            screenRect.X + screenRect.Width / 2,
            screenRect.Y + screenRect.Height / 2)).Bounds;

        // 整屏截图原样铺满窗口，选区只是遮罩上挖出的洞。
        var monitorCrop = new CroppedBitmap(shot.Image, new Int32Rect(
            _monitor.Left - shot.Bounds.X,
            _monitor.Top - shot.Bounds.Y,
            Math.Min(_monitor.Width, shot.Bounds.Width),
            Math.Min(_monitor.Height, shot.Bounds.Height)));
        monitorCrop.Freeze();
        _image.Source = monitorCrop;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Black;

        BuildLayout();
        UpdateGeometry();

        // 先建好 HWND 并直接铺满所在显示器：整个标注过程窗口位置和大小都不变，
        // 缩放选区时只重绘窗口内部，避免反复 SetWindowPos 导致画面被 DWM 拉伸弹动。
        new WindowInteropHelper(this).EnsureHandle();
        ApplyMonitorBounds();

        app.SetSuspended(false);
    }

    void BuildLayout()
    {
        _mask.Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));

        _imageHost.ClipToBounds = true;
        _imageHost.Children.Add(_layer);
        _imageHost.Children.Add(_overlay);
        _imageHost.Children.Add(_frame);
        _frame.IsHitTestVisible = false;

        _imageCanvas.ClipToBounds = true;
        _imageCanvas.Children.Add(_image);
        _imageCanvas.Children.Add(_mask);
        _imageCanvas.Children.Add(_imageHost);

        _layer.MouseLeftButtonDown += Layer_MouseLeftButtonDown;
        _layer.MouseMove += Layer_MouseMove;
        _layer.MouseLeftButtonUp += Layer_MouseLeftButtonUp;
        // 右键关闭要等这次右键消息处理完再执行，否则窗口先消失，
        // 松开的右键会落到下面的程序/桌面上，弹出对方的右键菜单。
        _layer.MouseRightButtonDown += (_, e) => e.Handled = true;
        MouseRightButtonDown += (_, e) => e.Handled = true;
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
        };

        _toolbar = BuildToolbar();
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _toolbarWidthDip = _toolbar.DesiredSize.Width;
        _toolbar.Width = _toolbarWidthDip;
        _imageCanvas.Children.Add(_toolbar);

        Content = _imageCanvas;

        PreviewKeyDown += Window_PreviewKeyDown;
        SelectTool(ToolKind.Rect);
        UpdateCursor(0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyMonitorBounds();
        UpdateGeometry();
        // WM_DPICHANGED 可能按建议矩形覆盖尺寸，布局完成后再定位一次
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyMonitorBounds();
            UpdateLayout();
            UpdateGeometry();
        }), DispatcherPriority.Loaded);
    }

    /// <summary>按当前选区重算遮罩洞、选区层和工具栏位置（窗口本身不动）。</summary>
    void UpdateGeometry()
    {
        var b = _shot.Bounds;
        int x = Math.Clamp(_rect.X, b.X, b.X + b.Width - 1);
        int y = Math.Clamp(_rect.Y, b.Y, b.Y + b.Height - 1);
        int w = Math.Clamp(_rect.Width, 1, b.X + b.Width - x);
        int h = Math.Clamp(_rect.Height, 1, b.Y + b.Height - y);
        _rect = new Int32Rect(x, y, w, h);

        double cw = _imageCanvas.ActualWidth > 1 ? _imageCanvas.ActualWidth : _monitor.Width / _scale;
        double ch = _imageCanvas.ActualHeight > 1 ? _imageCanvas.ActualHeight : _monitor.Height / _scale;
        double sx = cw / _monitor.Width;
        double sy = ch / _monitor.Height;

        _image.Width = cw;
        _image.Height = ch;

        double selX = (x - _monitor.Left) * sx;
        double selY = (y - _monitor.Top) * sy;
        double selW = w * sx;
        double selH = h * sy;

        _imageHost.Width = selW;
        _imageHost.Height = selH;
        Canvas.SetLeft(_imageHost, selX);
        Canvas.SetTop(_imageHost, selY);
        _frame.InvalidateVisual();

        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, cw, ch)));
        geometry.Children.Add(new RectangleGeometry(new Rect(selX, selY, selW, selH)));
        _mask.Data = geometry;
        _mask.Width = cw;
        _mask.Height = ch;

        double tbW = _toolbarWidthDip;
        bool below = selY + selH + ToolbarHeight <= ch || selY - ToolbarHeight < 0;
        double left = Math.Clamp(selX + (selW - tbW) / 2, 0, Math.Max(0, cw - tbW));
        double top = below ? selY + selH : selY - ToolbarHeight;
        top = Math.Clamp(top, 0, Math.Max(0, ch - ToolbarHeight));
        Canvas.SetLeft(_toolbar, left);
        Canvas.SetTop(_toolbar, top);
    }

    void ApplyMonitorBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST,
            _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, Native.SWP_NOACTIVATE);
    }

    // ---- 工具栏 ----

    Border BuildToolbar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _colorButton = MakeButton("", ShowColorMenu);
        _colorButton.Content = _colorSwatch;
        _colorButton.ToolTip = "颜色";

        var items = new Button[]
        {
            _colorButton,
            MakeToolButton("矩形", ToolKind.Rect),
            MakeToolButton("椭圆", ToolKind.Ellipse),
            MakeToolButton("箭头", ToolKind.Arrow),
            MakeToolButton("画笔", ToolKind.Pen),
            MakeToolButton("文字", ToolKind.Text),
            MakeButton("撤销", Undo),
            MakeButton("保存", SaveToFile),
            MakeButton("确认", SaveToClipboard),
            MakeButton("取消", Close),
        };

        // 按钮紧挨着，中间用 1px 竖线分隔
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0)
            {
                panel.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 1,
                    Height = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
                    IsHitTestVisible = false,
                });
            }
            panel.Children.Add(items[i]);
        }

        return new Border
        {
            Background = Brushes.Transparent,
            Height = ToolbarHeight,
            Child = panel,
        };
    }

    Button MakeButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---- 颜色 ----

    static readonly (string Name, Color Color)[] Palette =
    {
        ("红", Annotation.DefaultColor),
        ("橙", Color.FromRgb(0xFF, 0x8C, 0x00)),
        ("黄", Color.FromRgb(0xFF, 0xD4, 0x00)),
        ("绿", Color.FromRgb(0x22, 0xB1, 0x4C)),
        ("蓝", Color.FromRgb(0x1E, 0x90, 0xFF)),
        ("紫", Color.FromRgb(0x9B, 0x30, 0xFF)),
        ("黑", Colors.Black),
        ("白", Colors.White),
    };

    void ShowColorMenu()
    {
        if (_colorPopup is null) BuildColorPopup();
        UpdateColorMarks();

        var anchor = _colorButton.PointToScreen(new Point(0, _colorButton.ActualHeight));
        var work = WinForms.Screen.FromPoint(
            new System.Drawing.Point((int)anchor.X, (int)anchor.Y)).WorkingArea;
        // 下方放不下就向上弹出
        _colorPopup!.Placement = anchor.Y + 70 > work.Bottom ? PlacementMode.Top : PlacementMode.Bottom;
        _colorPopup.IsOpen = true;
    }

    void BuildColorPopup()
    {
        var grid = new UniformGrid { Columns = 4 };
        foreach (var (name, color) in Palette)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(3),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                Cursor = Cursors.Hand,
                ToolTip = name,
            };
            swatch.MouseLeftButtonUp += (_, e) =>
            {
                SetColor(color);
                _colorPopup!.IsOpen = false;
                e.Handled = true;
            };
            _paletteSwatches.Add((color, swatch));
            grid.Children.Add(swatch);
        }

        _colorPopup = new Popup
        {
            PlacementTarget = _colorButton,
            StaysOpen = false,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                Child = grid,
            },
        };
    }

    void UpdateColorMarks()
    {
        foreach (var (color, swatch) in _paletteSwatches)
        {
            bool selected = color == _layer.Color;
            swatch.BorderThickness = new Thickness(selected ? 2 : 1);
            swatch.BorderBrush = new SolidColorBrush(
                selected ? Colors.White : Color.FromRgb(0x66, 0x66, 0x66));
        }
    }

    void SetColor(Color color)
    {
        _layer.Color = color;
        _colorSwatch.Fill = new SolidColorBrush(color);
        if (_textBox is not null) _textBox.Foreground = new SolidColorBrush(color);
        UpdateColorMarks();
    }

    Button MakeToolButton(string text, ToolKind kind)
    {
        var button = MakeButton(text, () => SelectTool(kind));
        _toolButtons.Add((button, kind));
        return button;
    }

    void SelectTool(ToolKind kind)
    {
        _tool = kind;
        // 选中的工具用蓝色底 + 加粗，和其他工具区分开。
        var selected = new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF));
        var normal = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        foreach (var (button, k) in _toolButtons)
        {
            bool on = k == kind;
            button.Background = on ? selected : normal;
            button.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
        UpdateCursor(0);
    }

    // ---- 鼠标交互 ----

    void Layer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(_layer);

        // 双击 = 复制并关闭（文字工具除外，双击开始输入文字）
        if (e.ClickCount == 2 && _tool != ToolKind.Text)
        {
            CancelPreview();
            SaveToClipboard();
            e.Handled = true;
            return;
        }

        if (_tool == ToolKind.Text)
        {
            BeginText(point);
            e.Handled = true;
            return;
        }

        int edges = GetEdges(point);
        if (edges != 0)
        {
            BeginResize(edges);
            e.Handled = true;
            return;
        }

        _dragStart = point;
        _dragging = true;
        _preview = new Annotation
        {
            Kind = _tool,
            Color = _layer.Color,
            Start = point,
            End = point,
            Points = _tool == ToolKind.Pen ? new List<Point> { point } : null,
        };
        _layer.Preview = _preview;
        _layer.CaptureMouse();
        _layer.InvalidateVisual();
        e.Handled = true;
    }

    void Layer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_resizing)
        {
            ResizeMove();
            return;
        }
        if (!_dragging || _preview is null)
        {
            UpdateCursor(GetEdges(e.GetPosition(_layer)));
            return;
        }

        var point = e.GetPosition(_layer);
        if (_preview.Kind == ToolKind.Pen) _preview.Points!.Add(point);
        else _preview.End = point;
        _layer.InvalidateVisual();
    }

    void Layer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizing)
        {
            _resizing = false;
            _layer.ReleaseMouseCapture();
            UpdateCursor(GetEdges(e.GetPosition(_layer)));
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        _layer.ReleaseMouseCapture();

        var preview = _preview;
        _preview = null;
        _layer.Preview = null;

        if (preview is not null)
        {
            bool valid = preview.Kind switch
            {
                ToolKind.Pen => preview.Points is { Count: > 1 },
                _ => Math.Abs(preview.End.X - preview.Start.X) > 2 ||
                     Math.Abs(preview.End.Y - preview.Start.Y) > 2,
            };
            if (valid) _layer.Items.Add(preview);
        }
        _layer.InvalidateVisual();
    }

    void CancelPreview()
    {
        _dragging = false;
        _preview = null;
        _layer.Preview = null;
        if (_layer.IsMouseCaptured) _layer.ReleaseMouseCapture();
        _layer.InvalidateVisual();
    }

    // ---- 选区缩放 ----

    int GetEdges(Point p)
    {
        double w = _layer.ActualWidth;
        double h = _layer.ActualHeight;
        if (w <= 0 || h <= 0) return 0;
        if (p.X < 0 || p.Y < 0 || p.X > w || p.Y > h) return 0;

        int edges = 0;
        if (p.X <= EdgeHit) edges |= 1;
        else if (p.X >= w - EdgeHit) edges |= 4;
        if (p.Y <= EdgeHit) edges |= 2;
        else if (p.Y >= h - EdgeHit) edges |= 8;
        return edges;
    }

    void UpdateCursor(int edges)
    {
        Cursor cursor = edges switch
        {
            0 => _tool == ToolKind.Text ? Cursors.IBeam : Cursors.Cross,
            1 or 4 => Cursors.SizeWE,
            2 or 8 => Cursors.SizeNS,
            3 or 12 => Cursors.SizeNWSE,
            _ => Cursors.SizeNESW,
        };
        if (_layer.Cursor != cursor) _layer.Cursor = cursor;
    }

    void BeginResize(int edges)
    {
        _resizing = true;
        _resizeEdges = edges;
        Native.GetCursorPos(out _resizeStartCursor);
        _resizeStartRect = _rect;
        _layer.CaptureMouse();
        UpdateCursor(edges);
    }

    void ResizeMove()
    {
        if (!_resizing) return;
        Native.GetCursorPos(out var cursor);
        int dx = cursor.X - _resizeStartCursor.X;
        int dy = cursor.Y - _resizeStartCursor.Y;

        var start = _resizeStartRect;
        int left = start.X, top = start.Y;
        int right = start.X + start.Width, bottom = start.Y + start.Height;
        if ((_resizeEdges & 1) != 0) left += dx;
        if ((_resizeEdges & 4) != 0) right += dx;
        if ((_resizeEdges & 2) != 0) top += dy;
        if ((_resizeEdges & 8) != 0) bottom += dy;

        var b = _shot.Bounds;
        left = Math.Max(left, b.X);
        top = Math.Max(top, b.Y);
        right = Math.Min(right, b.X + b.Width);
        bottom = Math.Min(bottom, b.Y + b.Height);

        const int min = 16;
        if (right - left < min)
        {
            if ((_resizeEdges & 1) != 0) left = right - min;
            else right = left + min;
        }
        if (bottom - top < min)
        {
            if ((_resizeEdges & 2) != 0) top = bottom - min;
            else bottom = top + min;
        }

        _rect = new Int32Rect(left, top, right - left, bottom - top);
        UpdateGeometry();
    }

    // ---- 文字 ----

    void BeginText(Point point)
    {
        var box = new TextBox
        {
            MinWidth = 80,
            FontSize = 16,
            Foreground = new SolidColorBrush(_layer.Color),
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2),
        };
        Canvas.SetLeft(box, point.X);
        Canvas.SetTop(box, point.Y);
        _overlay.Children.Add(box);
        _textBox = box;

        box.LostKeyboardFocus += (_, _) => CommitText();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitText();
                e.Handled = true;
            }
        };
        box.Focus();
    }

    void CommitText()
    {
        var box = _textBox;
        if (box is null) return;
        _textBox = null;

        var text = box.Text.Trim();
        var point = new Point(Canvas.GetLeft(box), Canvas.GetTop(box));
        _overlay.Children.Remove(box);
        if (text.Length == 0) return;

        _layer.Items.Add(new Annotation
        {
            Kind = ToolKind.Text,
            Color = _layer.Color,
            Start = point,
            End = point,
            Text = text,
            FontSize = 16,
        });
        _layer.InvalidateVisual();
    }

    void CancelText()
    {
        var box = _textBox;
        if (box is null) return;
        _textBox = null;
        _overlay.Children.Remove(box);
    }

    // ---- 导出 ----

    BitmapSource RenderFinal()
    {
        int width = _rect.Width;
        int height = _rect.Height;
        double scale = _layer.ActualWidth > 1 ? width / _layer.ActualWidth : _scale;

        var b = _shot.Bounds;
        var background = new CroppedBitmap(
            _shot.Image, new Int32Rect(_rect.X - b.X, _rect.Y - b.Y, width, height));
        background.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(background, new Rect(0, 0, width, height)); // 背景 1:1
            dc.PushTransform(new ScaleTransform(scale, scale));
            AnnotationRenderer.Draw(dc, _layer.Items, _layer.Thickness, 1.0);
            dc.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        if (target.PixelWidth != width || target.PixelHeight != height)
            ConfigStore.Log($"导出尺寸异常: {target.PixelWidth}x{target.PixelHeight}, 期望 {width}x{height}");
        target.Freeze();
        return target;
    }

    void SaveToFile()
    {
        CommitText();
        var image = RenderFinal();

        string dir = Path.Combine(AppContext.BaseDirectory, "Screenshots");
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { ConfigStore.Log("创建截图目录失败", ex); }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png",
            DefaultExt = "png",
            AddExtension = true,
            FileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = dir,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            ScreenCapture.SavePng(image, dialog.FileName);
            Close();
        }
        catch (Exception ex)
        {
            ConfigStore.Log("保存截图失败", ex);
            MessageBox.Show(this, "保存失败：" + ex.Message, "WinTool");
        }
    }

    void SaveToClipboard()
    {
        CommitText();
        var image = RenderFinal();

        var data = new DataObject();
        data.SetImage(image);
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        stream.Position = 0;
        data.SetData("PNG", stream, true);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                Close();
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(100);
            }
        }
        ConfigStore.Log("复制到剪贴板失败：剪贴板被占用");
        MessageBox.Show(this, "复制失败：剪贴板被其他程序占用。", "WinTool");
    }

    // ---- 撤销 / 快捷键 ----

    void Undo()
    {
        if (_textBox is not null) { CancelText(); return; }
        if (_layer.Items.Count == 0) return;
        _layer.Items.RemoveAt(_layer.Items.Count - 1);
        _layer.InvalidateVisual();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textBox is not null)
        {
            if (e.Key == Key.Escape)
            {
                CancelText();
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            SaveToClipboard();
            e.Handled = true;
        }
        else if ((e.Key is Key.Left or Key.Right or Key.Up or Key.Down) &&
                 (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            Nudge(e.Key, Keyboard.Modifiers == ModifierKeys.Shift ? 10 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveToFile();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveToClipboard();
            e.Handled = true;
        }
    }

    void Nudge(Key key, int step)
    {
        var b = _shot.Bounds;
        int dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
        int dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
        int x = Math.Clamp(_rect.X + dx, b.X, b.X + b.Width - _rect.Width);
        int y = Math.Clamp(_rect.Y + dy, b.Y, b.Y + b.Height - _rect.Height);
        _rect = new Int32Rect(x, y, _rect.Width, _rect.Height);
        UpdateGeometry();
    }
}
