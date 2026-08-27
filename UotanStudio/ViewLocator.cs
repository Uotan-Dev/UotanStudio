using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio;

/// <summary>
/// <para>给定 ViewModel，返回对应视图（通过 <see cref="ViewModelViewRegistry"/> 查找）。</para>
/// Given a view model, returns the corresponding view via <see cref="ViewModelViewRegistry"/>.
/// </summary>
/// <remarks>
/// <para>不再使用反射；若 ViewModel 未注册，则返回带本地化提示的 <see cref="TextBlock"/>。</para>
/// No reflection is used; an unregistered ViewModel yields a <see cref="TextBlock"/> with a
/// localized "not found" message.
/// </remarks>
public class ViewLocator : IDataTemplate
{
    /// <summary>
    /// <para>根据数据项构建对应视图。</para>
    /// Builds the view for the given data item.
    /// </summary>
    /// <param name="param"><para>数据项（ViewModel）。</para>The data item (ViewModel).</param>
    /// <returns>
    /// <para>对应视图控件；未注册时为带提示文本的 <see cref="TextBlock"/>，<c>null</c> 入参返回 <c>null</c>。</para>
    /// The matching view control; a <see cref="TextBlock"/> with a hint when unregistered;
    /// <c>null</c> when <paramref name="param"/> is <c>null</c>.
    /// </returns>
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        var factory = ViewModelViewRegistry.Resolve(param.GetType());
        if (factory is not null)
        {
            return factory();
        }

        return new TextBlock
        {
            Text = LanguageService.Instance.GetString("ViewLocator_NotFound") + param.GetType().FullName
        };
    }

    /// <summary>
    /// <para>判断数据项是否为 <see cref="ViewModelBase"/>，决定是否由本模板处理。</para>
    /// Determines whether the data item is a <see cref="ViewModelBase"/> and thus handled by this template.
    /// </summary>
    /// <param name="data"><para>数据项。</para>The data item.</param>
    /// <returns>
    /// <para>当 <paramref name="data"/> 为 <see cref="ViewModelBase"/> 时返回 <c>true</c>。</para>
    /// <c>true</c> when <paramref name="data"/> is a <see cref="ViewModelBase"/>.
    /// </returns>
    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
