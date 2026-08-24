using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Areas.ProjectExplorer;
using UotanStudio.Core.Inspector;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels;

namespace UotanStudio.Areas.Properties;

/// <summary>
/// <para>属性面板视图模型：订阅选中变化消息，按选中对象类型填充键值对属性。</para>
/// Properties panel view model: subscribes to the selection-changed message and fills
/// key/value properties according to the selected object's type.
/// </summary>
public partial class PropertiesViewModel : ViewModelBase
{
    /// <summary>
    /// <para>获取属性条目集合。</para>
    /// Gets the collection of property entries.
    /// </summary>
    public ObservableCollection<PropertyItem> Items { get; } = [];

    /// <summary>
    /// <para>获取或设置面板标题（选中对象的名称）。</para>
    /// Gets or sets the panel title (the selected object's name).
    /// </summary>
    [ObservableProperty]
    private string? _title;

    /// <summary>
    /// <para>初始化属性面板并订阅选中变化消息。</para>
    /// Initializes the properties panel and subscribes to the selection-changed message.
    /// </summary>
    public PropertiesViewModel()
    {
        WeakReferenceMessenger.Default.Register<SelectionChangedMessage>(this, OnSelectionChanged);
    }

    private void OnSelectionChanged(object recipient, SelectionChangedMessage message)
    {
        Items.Clear();
        Title = null;

        switch (message.SelectedItem)
        {
            case ProjectTreeItem item:
                Title = item.Name;
                AddItem(LanguageService.Instance.GetString("Properties_Name"), item.Name);
                AddItem(LanguageService.Instance.GetString("Properties_Path"), item.FullPath);
                AddItem(LanguageService.Instance.GetString("Properties_Type"),
                    item.IsDirectory
                        ? LanguageService.Instance.GetString("Properties_Directory")
                        : LanguageService.Instance.GetString("Properties_File"));
                if (!item.IsDirectory && File.Exists(item.FullPath))
                {
                    var info = new FileInfo(item.FullPath);
                    AddItem(LanguageService.Instance.GetString("Properties_Size"),
                        info.Length.ToString(CultureInfo.InvariantCulture));
                    AddItem(LanguageService.Instance.GetString("Properties_Modified"),
                        info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                }

                if (!string.IsNullOrEmpty(item.Detail))
                {
                    AddItem(LanguageService.Instance.GetString("Properties_Detail"), item.Detail);
                }

                break;

            case InspectorNode node:
                Title = node.Name;
                AddItem(LanguageService.Instance.GetString("Properties_Name"), node.Name);
                if (!string.IsNullOrEmpty(node.Detail))
                {
                    AddItem(LanguageService.Instance.GetString("Properties_Detail"), node.Detail);
                }

                AddItem(LanguageService.Instance.GetString("Properties_ChildCount"),
                    node.Children.Count.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                Title = null;
                break;
        }
    }

    private void AddItem(string key, string value)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        Items.Add(new PropertyItem(key, value ?? string.Empty));
    }
}