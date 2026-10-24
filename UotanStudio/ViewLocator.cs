using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Dock.Controls.DeferredContentControl;
using UotanStudio.Services;

namespace UotanStudio;

/// <summary>
/// Given a view model, returns the corresponding control via <see cref="ViewModelViewRegistry"/>.
/// <para>给定数据对象，经 <see cref="ViewModelViewRegistry"/> 查找并返回对应控件。</para>
/// </summary>
public class ViewLocator : IRecyclingDataTemplate
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
    public Control? Build(object? param) => Build(param, null);

    /// <summary>
    /// Builds or recycles a control for Dock while avoiding a cached view that still has a visual parent.
    /// <para>为 Dock 构建或回收控件，并避免复用仍有视觉父级的缓存视图。</para>
    /// </summary>
    /// <param name="param">The bound data item. <para>绑定的数据项。</para></param>
    /// <param name="existing">The control currently presented by the caller. <para>调用方当前呈现的控件。</para></param>
    /// <returns>The control to present, or null for a null input. <para>要呈现的控件；输入为 null 时返回 null。</para></returns>
    public Control? Build(object? param, Control? existing)
    {
        if (param is null)
        {
            return null;
        }

        if (_viewCache.TryGetValue(param, out var cached))
        {
            if (ReferenceEquals(existing, cached) || TryReleaseCachedView(param, cached))
            {
                return cached;
            }

            _viewCache.Remove(param);
        }

        var view = CreateView(param);
        _viewCache.Add(param, view);
        return view;
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

    /// <summary>
    /// Releases a cached view from its deferred content presenter before the dock layout reparents it.
    /// <para>在 Dock 布局重新挂载缓存的视图前，将其从延迟内容呈现器中释放。</para>
    /// </summary>
    /// <param name="viewModel">The view model whose cached view is being moved. <para>正在移动且拥有缓存视图的视图模型。</para></param>
    internal static void ReleaseDeferredPresentation(object viewModel)
    {
        if (_viewCache.TryGetValue(viewModel, out var view))
        {
            _ = TryReleaseCachedView(viewModel, view);
        }
    }

    private static Control CreateView(object param)
    {
        var viewModelType = param.GetType();
        var factory = ViewModelViewRegistry.Resolve(viewModelType);
        if (factory is null)
        {
            return new TextBlock
            {
                Text = LanguageService.Instance.GetString("ViewLocator_NotFound") + viewModelType.FullName
            };
        }

        var view = factory();
        if (ViewModelViewRegistry.IsDocumentStyle(viewModelType))
        {
            if (view is UserControl editor && editor.Background is null)
            {
                editor.Background = Avalonia.Media.Brushes.Transparent;
            }
            view.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent,
                (_, _) => Services.Ui.Workspace.DocumentNotifications.Publish((Dock.Model.Mvvm.Controls.Document)param),
                Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        return view;
    }

    private static bool TryReleaseCachedView(object viewModel, Control view)
    {
        if (view.GetVisualParent() is null)
        {
            return true;
        }

        if (view.GetVisualAncestors().OfType<DeferredContentControl>().FirstOrDefault() is { } host)
        {
            host.ClearPresentation();
            host.SetCurrentValue(ContentControl.ContentProperty, null);
            host.ClearPresentation();
        }

        if (view.GetVisualParent() is Avalonia.Controls.Presenters.ContentPresenter presenter)
        {
            presenter.SetCurrentValue(ContentControl.ContentProperty, null);
            presenter.UpdateChild();
        }

        if (view.GetVisualParent() is not null)
        {
            LogService.Instance.Write(LogLevel.Debug, "Dock",
                $"Cached {view.GetType().Name} for {viewModel.GetType().Name} remained visually attached; a fresh view will be created.");
        }

        return view.GetVisualParent() is null;
    }
}