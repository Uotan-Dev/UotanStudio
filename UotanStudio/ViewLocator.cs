using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using UotanStudio.Services;

namespace UotanStudio;

/// <summary>
/// Given a view model, returns the corresponding control via <see cref="ViewModelViewRegistry"/>.
/// <para>给定数据对象，经 <see cref="ViewModelViewRegistry"/> 查找并返回对应控件。</para>
/// </summary>
public class ViewLocator : IDataTemplate
{
    private static readonly ConditionalWeakTable<object, Control> _viewCache = new();

    /// <summary>
    /// Builds a control for the given data item; the same VM instance reuses its cached control.
    /// <para>为给定数据项构建控件；同一 VM 实例复用其缓存的控件。</para>
    /// </summary>
    /// <param name="param">The bound data item. <para>绑定的数据项。</para></param>
    /// <returns>
    /// The matching control; a <see cref="TextBlock"/> with a hint when unregistered;
    /// <c>null</c> when <paramref name="param"/> is <c>null</c>.
    /// <para>对应控件；未注册时为带提示文本的 <see cref="TextBlock"/>；<c>null</c> 入参返回 <c>null</c>。</para>
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

        var viewModelType = param.GetType();
        var factory = ViewModelViewRegistry.Resolve(viewModelType);
        if (factory is not null)
        {
            var view = factory();
            if (ViewModelViewRegistry.IsDocumentStyle(viewModelType))
            {
                if (view is UserControl editor && editor.Background is null)
                {
                    editor.Background = Avalonia.Media.Brushes.Transparent;
                }
                view.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent,
                    (_, _) => UotanStudio.Services.Workspace.DocumentNotifications.Publish((Dock.Model.Mvvm.Controls.Document)param),
                    Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            }
            _viewCache.Add(param, view);
            return view;
        }

        return new TextBlock
        {
            Text = LanguageService.Instance.GetString("ViewLocator_NotFound") + param.GetType().FullName
        };
    }

    /// <summary>
    /// Determines whether a data item's type is registered in <see cref="ViewModelViewRegistry"/>,
    /// including dock tool and document entries, so this template handles it.
    /// <para>判断数据项的类型是否已在注册表中登记（含停靠工具与文档条目），即由本模板处理。</para>
    /// </summary>
    /// <param name="data">The data item. <para>数据项。</para></param>
    /// <returns>
    /// <c>true</c> when the type of <paramref name="data"/> is registered.
    /// <para>当 <paramref name="data"/> 的类型已注册时返回 <c>true</c>。</para>
    /// </returns>
    public bool Match(object? data)
    {
        return data is not null && ViewModelViewRegistry.Resolve(data.GetType()) is not null;
    }
}