using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIEverything.App;

internal static class PromotionScreenshotRenderer
{
    internal const int Width = 900;
    internal const int Height = 560;

    internal static void Render(string outputPath, bool empty = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!Path.IsPathFullyQualified(outputPath))
        {
            throw new ArgumentException("Promotion screenshot path must be absolute.", nameof(outputPath));
        }

        var view = new PromotionScreenshotView(empty)
        {
            Width = Width,
            Height = Height
        };
        var size = new Size(Width, Height);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            Width,
            Height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(view);
        bitmap.Freeze();

        var directory = Path.GetDirectoryName(outputPath) ??
                        throw new ArgumentException("Promotion screenshot path has no directory.", nameof(outputPath));
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}
