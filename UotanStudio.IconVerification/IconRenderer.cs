using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using UotanStudio.ViewModels;
using UotanStudio.Views.Controls;

namespace UotanStudio.IconVerification;

internal enum ForegroundExpectation
{
    Red,
    Blue,
    Green,
    White,
    Dark,
}

internal readonly record struct IconBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left + 1;

    public int Height => Bottom - Top + 1;
}

internal sealed record IconAnalysis(
    int Size,
    int PaintedPixels,
    IconBounds Bounds,
    int DominantPixels,
    double DominantRatio,
    bool TouchesEdge);

internal sealed class IconRenderer
{
    private readonly IconCatalog _catalog;
    private readonly string _iconDirectory;

    public IconRenderer(IconCatalog catalog, string iconDirectory)
    {
        _catalog = catalog;
        _iconDirectory = iconDirectory;
        Directory.CreateDirectory(iconDirectory);
    }

    public (IconAnalysis Analysis, string Path) RenderAndAnalyze(
        IconTarget target,
        int size,
        Color background,
        Color foreground,
        ForegroundExpectation expectation,
        string? suffix = null)
    {
        var icon = CreateIcon(target, size, foreground);
        var fileSuffix = suffix is null ? string.Empty : $".{suffix}";
        var path = Path.Combine(_iconDirectory, $"{SafeName(target.DisplayName)}{fileSuffix}.png");
        var frame = RenderContent(icon, size, size, background);
        var analysis = Analyze(frame, background, expectation);
        frame.Save(path, new PngBitmapEncoderOptions());
        return (analysis, path);
    }

    public string RenderSheet(Control content, int width, int height, string path)
    {
        var frame = RenderContent(content, width, height, Colors.White);
        frame.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    public Control CreateIcon(IconTarget target, int size, Color foreground)
    {
        if (!target.IsAppIcon)
        {
            var icon = _catalog.Create(target.TypeName);
            icon.Width = size;
            icon.Height = size;
            icon.Stroke1 = new SolidColorBrush(foreground);
            return icon;
        }

        return new AppIcon
        {
            Kind = Enum.Parse<AppIconKind>(target.TypeName, ignoreCase: false),
            Width = size,
            Height = size,
            Foreground = new SolidColorBrush(foreground),
            Padding = default,
            BorderThickness = default,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
    }

    public static WriteableBitmap RenderContent(Control content, int width, int height, Color background)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            CanResize = false,
            WindowDecorations = WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = new SolidColorBrush(background),
            Content = content,
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            return window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException($"Headless rendering returned no frame for {width}x{height}.");
        }
        finally
        {
            window.Close();
        }
    }

    public static IconAnalysis Analyze(WriteableBitmap frame, Color background, ForegroundExpectation expectation)
    {
        var (pixels, width, height, stride, bgra) = CopyPixels(frame);
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        var painted = 0;
        var dominant = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = ReadPixel(pixels, stride, bgra, x, y);
                if (!IsPainted(r, g, b, background))
                {
                    continue;
                }

                painted++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
                if (MatchesForeground(r, g, b, expectation))
                {
                    dominant++;
                }
            }
        }

        if (painted == 0)
        {
            return new IconAnalysis(width, 0, new IconBounds(0, 0, -1, -1), 0, 0, false);
        }

        var bounds = new IconBounds(left, top, right, bottom);
        var touchesEdge = left <= 0 || top <= 0 || right >= width - 1 || bottom >= height - 1;
        return new IconAnalysis(width, painted, bounds, dominant, (double)dominant / painted, touchesEdge);
    }

    public static int CountPainted(WriteableBitmap frame, Color background, PixelRect region)
    {
        var (pixels, width, height, stride, bgra) = CopyPixels(frame);
        var left = Math.Max(0, region.X);
        var top = Math.Max(0, region.Y);
        var right = Math.Min(width, region.Right);
        var bottom = Math.Min(height, region.Bottom);
        var painted = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var (r, g, b) = ReadPixel(pixels, stride, bgra, x, y);
                if (IsPainted(r, g, b, background))
                {
                    painted++;
                }
            }
        }

        return painted;
    }

    private static (byte[] Pixels, int Width, int Height, int Stride, bool Bgra) CopyPixels(WriteableBitmap frame)
    {
        using var framebuffer = frame.Lock();
        var width = framebuffer.Size.Width;
        var height = framebuffer.Size.Height;
        var stride = framebuffer.RowBytes;
        var bytes = new byte[stride * height];
        Marshal.Copy(framebuffer.Address, bytes, 0, bytes.Length);
        var bgra = framebuffer.Format == PixelFormats.Bgra8888
            || framebuffer.Format.ToString().Contains("Bgra", StringComparison.OrdinalIgnoreCase);
        return (bytes, width, height, stride, bgra);
    }

    private static (byte R, byte G, byte B) ReadPixel(byte[] pixels, int stride, bool bgra, int x, int y)
    {
        var offset = y * stride + x * 4;
        return bgra
            ? (pixels[offset + 2], pixels[offset + 1], pixels[offset])
            : (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    private static bool IsPainted(byte r, byte g, byte b, Color background) =>
        Math.Abs(r - background.R) + Math.Abs(g - background.G) + Math.Abs(b - background.B) > 24;

    private static bool MatchesForeground(byte r, byte g, byte b, ForegroundExpectation expectation) => expectation switch
    {
        ForegroundExpectation.Red => r - Math.Max(g, b) > 20,
        ForegroundExpectation.Blue => b - Math.Max(r, g) > 20,
        ForegroundExpectation.Green => g - Math.Max(r, b) > 20,
        ForegroundExpectation.White => r > 200 && g > 200 && b > 200,
        ForegroundExpectation.Dark => r < 80 && g < 80 && b < 80,
        _ => false,
    };

    public static string SafeName(string name) =>
        name.Replace('.', '_').Replace('<', '_').Replace('>', '_').Replace(':', '_');
}