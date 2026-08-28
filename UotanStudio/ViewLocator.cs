using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using UotanStudio.Services;

namespace UotanStudio;

/// <summary>
/// <para>给定 ViewModel，返回对应视图（通过 <see cref="ViewModelViewRegistry"/> 查找）。</para>
/// Given a view model, returns the corresponding view via <see cref="ViewModelViewRegistry"/>.
/// </summary>
/// <remarks>
/// <para>不再使用反射；若 ViewModel 未注册，则返回带本地化提示的 <see cref="TextBlock"/>。
/// 已注册的 ViewModel 按实例缓存已创建的视图：Dock 标签切换时 ContentPresenter 会重新应用
/// 模板并再次调用本方法，若不缓存会为每个打开中的文档反复重建视图（每个新视图都持有大文本
/// 的编辑器 Document，导致来回切换标签时内存持续增长）。缓存以弱引用键关联 VM 实例，VM 被
/// 回收后条目自动消失，不会反向延长 VM 生命周期。</para>
/// No reflection is used; an unregistered ViewModel yields a <see cref="TextBlock"/> with a
/// localized "not found" message. Views for registered ViewModels are cached per instance:
/// when Dock tabs are switched, the ContentPresenter re-applies the template and calls this
/// method again — without caching, every open document would get a fresh view (each holding an
/// editor document with large text), making memory grow steadily while switching tabs back and
/// forth. The cache keys on the VM instance via a weak table, so entries vanish once the VM is
/// collected and never extend the VM's lifetime.
/// </remarks>
public class ViewLocator : IDataTemplate
{
    private static readonly ConditionalWeakTable<object, Control> _viewCache = new();

    /// <summary>
    /// <para>根据数据项构建对应视图；同一 VM 实例复用已缓存的视图。</para>
    /// Builds the view for the given data item; the same VM instance reuses its cached view.
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

        if (_viewCache.TryGetValue(param, out var cached))
        {
            return cached;
        }

        var factory = ViewModelViewRegistry.Resolve(param.GetType());
        if (factory is not null)
        {
            var view = factory();
            _viewCache.Add(param, view);
            return view;
        }

        return new TextBlock
        {
            Text = LanguageService.Instance.GetString("ViewLocator_NotFound") + param.GetType().FullName
        };
    }

    /// <summary>
    /// <para>判断数据项是否为注册表中登记的 ViewModel（含 Dock 面板/文档的 <c>Tool</c>/<c>Document</c> 派生 VM），
    /// 决定是否由本模板处理。</para>
    /// Determines whether the data item is a ViewModel registered in
    /// <see cref="ViewModelViewRegistry"/> (including <c>Tool</c>/<c>Document</c>-derived view
    /// models used by dock panels and documents), and thus handled by this template.
    /// </summary>
    /// <param name="data"><para>数据项。</para>The data item.</param>
    /// <returns>
    /// <para>当 <paramref name="data"/> 的类型已注册时返回 <c>true</c>。</para>
    /// <c>true</c> when the type of <paramref name="data"/> is registered.
    /// </returns>
    public bool Match(object? data)
    {
        return data is not null && ViewModelViewRegistry.Resolve(data.GetType()) is not null;
    }
}
