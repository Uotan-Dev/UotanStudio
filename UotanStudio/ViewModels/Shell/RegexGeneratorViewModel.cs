using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.RegexInfer;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// View model for the regular expression generator window.
/// <para>正则表达式生成器窗口的视图模型。</para>
/// </summary>
public sealed partial class RegexGeneratorViewModel : ObservableObject
{
    /// <summary>
    /// Source data to infer the pattern from.
    /// <para>用于推导模式的原始数据。</para>
    /// </summary>
    [ObservableProperty]
    private string _sourceText = string.Empty;

    /// <summary>
    /// Target content to find within the source data.
    /// <para>在原始数据中待查找的目标内容。</para>
    /// </summary>
    [ObservableProperty]
    private string _findContent = string.Empty;

    /// <summary>
    /// Generated regular expression pattern.
    /// <para>生成的正则表达式模式。</para>
    /// </summary>
    [ObservableProperty]
    private string _pattern = string.Empty;

    /// <summary>
    /// Status or feedback message.
    /// <para>状态或反馈提示消息。</para>
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// Generates a regular expression based on <see cref="SourceText"/> and <see cref="FindContent"/>.
    /// <para>基于 <see cref="SourceText"/> 与 <see cref="FindContent"/> 生成正则表达式。</para>
    /// </summary>
    [RelayCommand]
    public void Generate()
    {
        if (string.IsNullOrWhiteSpace(SourceText) || string.IsNullOrWhiteSpace(FindContent))
        {
            StatusMessage = LanguageService.Instance.GetString("RegexGenerator_EmptyInput");
            return;
        }

        var targets = FindContent
            .Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targets.Length == 0)
        {
            targets = [FindContent.Trim()];
        }

        var validTargets = targets.Where(t => SourceText.Contains(t, StringComparison.Ordinal)).ToArray();

        if (validTargets.Length > 0)
        {
            try
            {
                var options = new RegexInferenceOptions
                {
                    PreferAnchors = false,
                    TopResults = 1,
                };
                var candidates = RegexInfer.RegexInfer.Infer(SourceText, validTargets, options);
                if (candidates.Count > 0)
                {
                    Pattern = candidates[0].Pattern;
                    StatusMessage = string.Empty;
                    return;
                }
            }
            catch
            {
                // Fallback to literal pattern below
            }
        }

        // Fallback to escaped regex pattern
        Pattern = Regex.Escape(FindContent);
        StatusMessage = validTargets.Length == 0
            ? LanguageService.Instance.GetString("RegexGenerator_NoMatchFallback")
            : string.Empty;
    }
}