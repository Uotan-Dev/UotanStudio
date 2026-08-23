using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using UotanStudio.Areas.Device;
using UotanStudio.Areas.Log;
using UotanStudio.Areas.NodeEditor;
using UotanStudio.Areas.Settings;
using UotanStudio.Areas.Workspace;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// <para>根据视图模型，返回对应的视图（如果可能）。</para>
/// Uses an explicit type map instead of reflection so the application stays
/// trimming/AOT safe (a reflection-based lookup would be trimmed away).
/// <para>使用显式类型映射而非反射，以保证应用可被裁剪/AOT（基于反射的查找会被裁剪掉）。</para>
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> ViewMap = new()
    {
        [typeof(MainViewModel)] = () => new Views.MainView(),
        [typeof(DeviceViewModel)] = () => new Areas.Device.DeviceView(),
        [typeof(WorkspaceViewModel)] = () => new Areas.Workspace.WorkspaceView(),
        [typeof(LogViewModel)] = () => new Areas.Log.LogView(),
        [typeof(SettingsViewModel)] = () => new Areas.Settings.SettingsView(),
        [typeof(NodeEditorViewModel)] = () => new Areas.NodeEditor.NodeEditorView(),
    };

    /// <summary>
    /// Builds the view for the specified view model parameter.
    /// <para>为指定的视图模型参数构建视图。</para>
    /// </summary>
    /// <param name="param">The view model parameter.</param>
    public Control? Build(object? param)
    {
        return param is null
            ? null
            : ViewMap.TryGetValue(param.GetType(), out var factory)
            ? factory()
            : new TextBlock { Text = LanguageService.Instance.GetString("ViewLocator_NotFound") + param.GetType().FullName };
    }

    /// <summary>
    /// Determines whether the data is a ViewModelBase.
    /// <para>判断数据是否为 ViewModelBase。</para>
    /// </summary>
    public bool Match(object? data) => data is ViewModelBase;
}