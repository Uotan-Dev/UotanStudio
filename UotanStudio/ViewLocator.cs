using Avalonia.Controls;
using Avalonia.Controls.Templates;
using System;
using System.Diagnostics.CodeAnalysis;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// <para>根据视图模型，返回对应的视图（如果可能）。</para>
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    /// <summary>
    /// Builds the view for the specified view model parameter.
    /// <para>为指定的视图模型参数构建视图。</para>
    /// </summary>
    /// <param name="param">The view model parameter.</param>
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = LanguageService.Instance.GetString("ViewLocator_NotFound") + name };
    }

    /// <summary>
    /// Determines whether the data is a ViewModelBase.
    /// <para>判断数据是否为 ViewModelBase。</para>
    /// </summary>
    public bool Match(object? data) => data is ViewModelBase;
}