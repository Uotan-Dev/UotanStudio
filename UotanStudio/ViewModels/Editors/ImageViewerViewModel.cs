using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Images;

namespace UotanStudio.ViewModels.Editors;

/// <summary>
/// Read-only image document: decodes a picture through the SIMD codec facade, holds its preview and
/// the attributes that the properties panel publishes.
/// <para>只读图片文档：经 SIMD 编解码门面解码图片，持有预览与供属性面板发布的文件属性。</para>
/// </summary>
public partial class ImageViewerViewModel : Document
{
    /// <summary>The localized loading status.<para>本地化的加载状态文案。</para></summary>
    public static string LoadingLabel => LanguageService.Instance.GetString("Dock_ImageViewer_Loading");

    /// <summary>The localized empty-preview hint.<para>本地化的空预览提示。</para></summary>
    public static string EmptyLabel => LanguageService.Instance.GetString("Dock_ImageViewer_Empty");

    /// <summary>The localized dimensions label.<para>本地化的尺寸标签。</para></summary>
    public static string DimensionsLabel => LanguageService.Instance.GetString("Dock_ImageViewer_Dimensions");

    /// <summary>The localized format label.<para>本地化的格式标签。</para></summary>
    public static string FormatLabel => LanguageService.Instance.GetString("Dock_ImageViewer_Format");

    /// <summary>Absolute path of the opened picture.<para>已打开图片的绝对路径。</para></summary>
    [ObservableProperty] private string? _filePath;

    /// <summary>Decoded preview, or null while loading or when decoding is unsupported.<para>解码预览；加载中或无法解码时为 null。</para></summary>
    [ObservableProperty] private Bitmap? _preview;

    /// <summary>Attributes read from the file; null before the first inspection.<para>从文件读取的属性；首次探测前为 null。</para></summary>
    [ObservableProperty] private ImageFileProperties? _properties;

    /// <summary>Status text shown in place of the preview.<para>替代预览显示的状态文案。</para></summary>
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Whether an inspection is running.<para>是否正在探测。</para></summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Whether a decoded preview is available.<para>是否已有解码预览。</para></summary>
    public bool HasPreview => Preview is not null;

    /// <summary>Stored pixel dimensions for the status strip.<para>状态栏显示的存储像素尺寸。</para></summary>
    public string DimensionsText => Properties?.Dimensions ?? string.Empty;

    /// <summary>Codec format name for the status strip.<para>状态栏显示的编解码格式名。</para></summary>
    public string FormatText => Properties?.CodecFormat ?? string.Empty;

    /// <summary>
    /// Loads the picture at <paramref name="path"/>, reading its attributes and building a bounded preview
    /// off the UI thread; failures are reported as status text instead of throwing.
    /// <para>载入 <paramref name="path"/> 指向的图片：在 UI 线程外读取属性并生成有界预览；失败以状态文案上报而不抛异常。</para>
    /// </summary>
    /// <param name="path">Absolute or relative path of the picture.<para>图片的绝对或相对路径。</para></param>
    /// <param name="cancellationToken">Cancels the inspection.<para>取消探测。</para></param>
    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        FilePath = Path.GetFullPath(path);
        IsBusy = true;
        StatusText = LoadingLabel;
        ReplacePreview(null);

        var snapshot = await Task.Run(
            () => ImageFileInspector.Inspect(FilePath, cancellationToken)).ConfigureAwait(true);

        IsBusy = false;
        Properties = snapshot.Properties;
        ReplacePreview(snapshot.PreviewPng);
        StatusText = snapshot.ErrorKey is { } key
            ? LanguageService.Instance.GetString(key)
            : snapshot.PreviewPng is null ? EmptyLabel : string.Empty;
    }

    /// <summary>
    /// Releases the decoded bitmap held by this document.
    /// <para>释放本文档持有的解码位图。</para>
    /// </summary>
    public void Cleanup() => ReplacePreview(null);

    private void ReplacePreview(byte[]? png)
    {
        var previous = Preview;
        Bitmap? next = null;
        if (png is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(png, writable: false);
                next = new Bitmap(stream);
            }
            catch (Exception)
            {
                next = null;
            }
        }

        Preview = next;
        previous?.Dispose();
    }

    partial void OnPreviewChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPreview));

    partial void OnPropertiesChanged(ImageFileProperties? value)
    {
        OnPropertyChanged(nameof(DimensionsText));
        OnPropertyChanged(nameof(FormatText));
    }
}