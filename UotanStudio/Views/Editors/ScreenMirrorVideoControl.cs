using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>Renders YUV planes through Skia's active GPU backend, with the existing bitmap fallback.</summary>
public sealed class ScreenMirrorVideoControl : Control
{
    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not ScreenMirrorViewModel model) return;
        var bounds = new Rect(Bounds.Size);
        if (!model.GpuPresenter.IsAvailable && model.Frame is { } bitmap)
        {
            var size = bitmap.Size;
            double scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
            var destination = new Rect((bounds.Width - size.Width * scale) / 2, (bounds.Height - size.Height * scale) / 2, size.Width * scale, size.Height * scale);
            context.DrawImage(bitmap, new Rect(size), destination);
        }
        context.Custom(model.GpuPresenter.CreateOperation(bounds));
    }
}