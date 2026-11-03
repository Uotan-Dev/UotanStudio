using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using UotanStudio.Composition;
using UotanStudio.Services;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// Regular expression generator window: multiline source text, target content, and pattern inference.
/// <para>正则表达式生成器窗口：多行原始数据、目标内容与模式推导。</para>
/// </summary>
public partial class RegexGeneratorWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RegexGeneratorWindow"/> class.
    /// <para>初始化 <see cref="RegexGeneratorWindow"/> 类的新实例。</para>
    /// </summary>
    public RegexGeneratorWindow() : this(null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexGeneratorWindow"/> class with optional initial data.
    /// <para>使用可选初始数据初始化 <see cref="RegexGeneratorWindow"/> 类的新实例。</para>
    /// </summary>
    /// <param name="sourceText">Initial source data. <para>初始原始数据。</para></param>
    /// <param name="findText">Initial find content. <para>初始查找内容。</para></param>
    public RegexGeneratorWindow(string? sourceText, string? findText)
    {
        InitializeComponent();
        DataContext = ServiceHub.TryGet<RegexGeneratorViewModel>() ?? new RegexGeneratorViewModel();
        SetInitialData(sourceText, findText);
    }

    private RegexGeneratorViewModel ViewModel => (RegexGeneratorViewModel)DataContext!;

    /// <summary>
    /// Sets or updates the initial source data and find content.
    /// <para>设置或更新初始原始数据和查找内容。</para>
    /// </summary>
    /// <param name="sourceText">Initial source data. <para>初始原始数据。</para></param>
    /// <param name="findText">Initial find content. <para>初始查找内容。</para></param>
    public void SetInitialData(string? sourceText, string? findText)
    {
        if (!string.IsNullOrEmpty(sourceText))
        {
            ViewModel.SourceText = sourceText;
        }

        if (!string.IsNullOrEmpty(findText))
        {
            ViewModel.FindContent = findText;
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.Pattern) || Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(ViewModel.Pattern);
            await clipboard.FlushAsync();
            ViewModel.StatusMessage = LanguageService.Instance.GetString("RegexGenerator_Copied");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "RegexGenerator", $"Copy failed: {ex.Message}");
        }
    }
}