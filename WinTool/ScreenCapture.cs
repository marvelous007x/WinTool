using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace WinTool;

public sealed class ScreenShot
{
    public BitmapSource Image { get; }
    /// <summary>截取范围，虚拟屏幕物理像素坐标。</summary>
    public Int32Rect Bounds { get; }

    public ScreenShot(BitmapSource image, Int32Rect bounds)
    {
        Image = image;
        Bounds = bounds;
    }
}

public static class ScreenCapture
{
    /// <summary>按虚拟屏幕物理像素范围截取整个桌面（含所有显示器）。</summary>
    public static ScreenShot CaptureVirtualScreen()
    {
        int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("无法获取屏幕尺寸。");

        using var bitmap = new Drawing.Bitmap(w, h, DrawingImaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            // 直接用 GDI BitBlt：.NET 8 的 CopyFromScreen 会拒绝 SourceCopy|CaptureBlt 组合值，
            // 而 CAPTUREBLT 是截到分层窗口（如开始菜单）必需的。
            IntPtr destDc = g.GetHdc();
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            try
            {
                if (!Native.BitBlt(destDc, 0, 0, w, h, screenDc, x, y, Native.SRCCOPY | Native.CAPTUREBLT))
                    throw new InvalidOperationException($"屏幕截取失败，错误码 {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                g.ReleaseHdc(destDc);
            }
        }

        // 直接搬运像素构造 BitmapSource，避免整屏 PNG 编码 + 解码的往返开销
        // （之前热键按下后要等两三百毫秒，主要耗在这里）。
        var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, w, h),
            DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * h];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bitmap.UnlockBits(data);

        // Bgr32 忽略 GDI 位图里未定义的 alpha，保证画面不透明。
        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, data.Stride);
        image.Freeze();
        return new ScreenShot(image, new Int32Rect(x, y, w, h));
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
