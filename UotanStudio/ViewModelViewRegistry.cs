using Avalonia.Controls;
using UotanStudio.ViewModels.Devices;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Devices;
using UotanStudio.Views.Editors;
using UotanStudio.Views.Workspace;

namespace UotanStudio;

/// <summary>
/// <para>显式 ViewModel→View 注册表，替代基于反射的 <c>ViewLocator</c>。</para>
/// Explicit ViewModel→View registry, replacing the reflection-based <c>ViewLocator</c>.
/// </summary>
/// <remarks>
/// <para>注册表在静态构造函数中一次性登记全部 VM→View 工厂，<c>ViewLocator</c> 据此查找。
/// 此设计消除运行时反射，使 ViewLocator 与 AOT 兼容；新增 ViewModel 须在此同步登记。</para>
/// The registry is populated once in the static constructor with every VM→View factory, and
/// <c>ViewLocator</c> looks views up from it. This removes runtime reflection, keeping
/// ViewLocator AOT-compatible; new ViewModels must be registered here as well.
/// </remarks>
internal static class ViewModelViewRegistry
{
    /// <summary>
    /// <para>VM 类型 → 视图工厂 的映射表。</para>
    /// Map of VM type → view factory.
    /// </summary>
    private static readonly Dictionary<Type, Func<Control>> _registry = new()
    {
        [typeof(FileViewModel)] = () => new FileView(),
        [typeof(DtbEditorViewModel)] = () => new DtbEditorView(),
        [typeof(DexEditorViewModel)] = () => new DexEditorView(),
        [typeof(ManifestEditorViewModel)] = () => new ManifestEditorView(),
        [typeof(ArscEditorViewModel)] = () => new ArscEditorView(),
        [typeof(HexEditorViewModel)] = () => new HexEditorView(),
        [typeof(ApkEditorViewModel)] = () => new ApkEditorView(),
        [typeof(FirmwareInfoViewModel)] = () => new FirmwareInfoView(),
        [typeof(ImageViewModel)] = () => new ImageView(),
        [typeof(ProjectExplorerViewModel)] = () => new ProjectExplorerView(),
        [typeof(ProjectSettingViewModel)] = () => new ProjectSettingView(),
        [typeof(PropertiesViewModel)] = () => new PropertiesView(),
        [typeof(DeviceViewModel)] = () => new DeviceView(),
    };

    /// <summary>
    /// <para>按 ViewModel 类型查找对应视图工厂；未注册时返回 <c>null</c>。</para>
    /// Looks up the view factory for the given ViewModel type; returns <c>null</c> when unregistered.
    /// </summary>
    /// <param name="viewModelType"><para>ViewModel 类型。</para>The ViewModel type.</param>
    /// <returns>
    /// <para>视图工厂委托；未注册时为 <c>null</c>。</para>
    /// The view-factory delegate; <c>null</c> when unregistered.
    /// </returns>
    public static Func<Control>? Resolve(Type viewModelType)
    {
        return _registry.TryGetValue(viewModelType, out var factory) ? factory : null;
    }
}
