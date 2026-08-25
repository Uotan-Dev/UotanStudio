using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Firmware.Common;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels;

/// <summary>
/// <para>属性视图模型，动态显示当前选中节点的属性或选中镜像的详细参数，
/// 订阅节点编辑器选中变更消息与镜像选中变更消息。</para>
/// Properties view model, dynamically displaying the currently selected node's properties or the
/// selected image's detailed parameters, subscribed to node-editor and image selection changes.
/// </summary>
internal partial class PropertiesViewModel : Tool
{
    /// <summary>
    /// <para>单个属性行视图模型，承载名称-值对。</para>
    /// Single property row view model, carrying a name-value pair.
    /// </summary>
    public sealed partial class PropertyRow : ObservableObject
    {
        /// <summary>
        /// <para>属性名称。</para>
        /// Property name.
        /// </summary>
        [ObservableProperty] private string _name = string.Empty;

        /// <summary>
        /// <para>属性值文本。</para>
        /// Property value text.
        /// </summary>
        [ObservableProperty] private string _value = string.Empty;

        /// <summary>
        /// <para>创建属性行。</para>
        /// Creates a property row.
        /// </summary>
        public PropertyRow(string name, string value)
        {
            _name = name;
            _value = value;
        }
    }

    /// <summary>
    /// <para>当前显示的属性行集合，UI 绑定此项。</para>
    /// Collection of property rows currently displayed, bound by the UI.
    /// </summary>
    public ObservableCollection<PropertyRow> Properties { get; } = [];

    /// <summary>
    /// <para>当前选中的节点标题，用于显示。</para>
    /// Title of the currently selected node, for display.
    /// </summary>
    [ObservableProperty] private string _selectedNodeTitle = string.Empty;

    /// <summary>
    /// <para>是否已选中节点。</para>
    /// Whether a node is selected.
    /// </summary>
    [ObservableProperty] private bool _hasSelection;

    /// <summary>
    /// <para>当前展示的内容类型（节点或镜像）。</para>
    /// The type of content currently displayed (node or image).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentTypeDisplay))]
    private PropertiesContentType _contentType = PropertiesContentType.None;

    /// <summary>
    /// <para>本地化后的内容类型标签（属性面板头部徽章）。</para>
    /// Localized content type label (badge in the properties panel header).
    /// </summary>
    public string ContentTypeDisplay
    {
        get
        {
            var typeName = ContentType switch
            {
                PropertiesContentType.Node => LanguageService.Instance.GetString("Dock_Properties_Type_Node"),
                PropertiesContentType.Image => LanguageService.Instance.GetString("Dock_Properties_Type_Image"),
                PropertiesContentType.FileAnalysis => LanguageService.Instance.GetString("Dock_Properties_Type_FileAnalysis"),
                _ => string.Empty,
            };

            return string.Format(LanguageService.Instance.GetString("Dock_Properties_TypeFormat"), typeName);
        }
    }

    /// <summary>
    /// <para>构造属性视图模型，订阅节点选中变更消息与镜像选中变更消息并显示无选择占位。</para>
    /// Creates the properties view model, subscribing to node-selection-changed and image-selection-changed
    /// messages and showing the no-selection placeholder.
    /// </summary>
    public PropertiesViewModel()
    {
        RefreshForNode(null);
        WeakReferenceMessenger.Default.Register<NodeSelectionChangedMessage>(this, OnNodeSelectionChanged);
        WeakReferenceMessenger.Default.Register<ImageSelectionChangedMessage>(this, OnImageSelectionChanged);
        WeakReferenceMessenger.Default.Register<FileAnalysisCompletedMessage>(this, OnFileAnalysisCompleted);
    }

    /// <summary>
    /// <para>响应节点选中变更消息，刷新属性行。</para>
    /// Responds to node-selection-changed messages by refreshing the property rows.
    /// </summary>
    private void OnNodeSelectionChanged(object recipient, NodeSelectionChangedMessage message) => RefreshForNode(message.Node);

    /// <summary>
    /// <para>响应镜像选中变更消息，刷新属性行。</para>
    /// Responds to image-selection-changed messages by refreshing the property rows.
    /// </summary>
    private void OnImageSelectionChanged(object recipient, ImageSelectionChangedMessage message) => RefreshForImage(message.Image);

    /// <summary>
    /// <para>响应文件分析完成消息，刷新属性行展示分析结果。</para>
    /// Responds to file-analysis-completed messages by refreshing property rows with analysis results.
    /// </summary>
    private void OnFileAnalysisCompleted(object recipient, FileAnalysisCompletedMessage message) => RefreshForFileAnalysis(message);

    /// <summary>
    /// <para>刷新指定节点的属性行。节点为 <c>null</c> 时显示占位。</para>
    /// Refreshes property rows for the specified node. Shows a placeholder when the node is <c>null</c>.
    /// </summary>
    public void RefreshForNode(FlowNodeViewModel? node)
    {
        if (node is null && ContentType != PropertiesContentType.Node)
        {
            return;
        }

        Properties.Clear();
        if (node == null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.Node;
        HasSelection = true;
        SelectedNodeTitle = node.Title;

        var svc = LanguageService.Instance;
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_ImageName"), node.Title));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_ImageType"), node.Category.ToString()));
        Properties.Add(new PropertyRow("Status", node.ExecutionStatus.ToString()));
        Properties.Add(new PropertyRow("Location", $"{node.Location.X}, {node.Location.Y}"));
        Properties.Add(new PropertyRow("Inputs", node.Input.Count.ToString()));
        Properties.Add(new PropertyRow("Outputs", node.Output.Count.ToString()));

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for node '{node.Title}'");
    }

    /// <summary>
    /// <para>刷新指定镜像的详细参数。镜像为 <c>null</c> 时显示占位。</para>
    /// Refreshes detailed parameters for the specified image. Shows a placeholder when the image is <c>null</c>.
    /// </summary>
    public void RefreshForImage(ImageEntryViewModel? image)
    {
        Properties.Clear();
        if (image is null)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.Image;
        HasSelection = true;
        SelectedNodeTitle = image.DisplayName;

        var svc = LanguageService.Instance;
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_ImageName"), image.DisplayName));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Path"), image.RelativePath));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Format"), image.Format));
        Properties.Add(new PropertyRow(svc.GetString("Dock_Properties_Size"), $"{image.SizeBytes:N0} bytes"));

        // 尝试读取固件详细参数
        _ = RefreshImageDetailsAsync(image);

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for image '{image.DisplayName}'");
    }

    /// <summary>
    /// <para>刷新指定文件分析结果的属性行。分析为 <c>null</c> 时显示占位。</para>
    /// Refreshes property rows for the specified file analysis results. Shows a placeholder when analysis is <c>null</c>.
    /// </summary>
    public void RefreshForFileAnalysis(FileAnalysisCompletedMessage? analysis)
    {
        Properties.Clear();
        if (analysis is null || analysis.Analysis.Count == 0)
        {
            SelectedNodeTitle = string.Empty;
            HasSelection = false;
            ContentType = PropertiesContentType.None;
            return;
        }

        ContentType = PropertiesContentType.FileAnalysis;
        HasSelection = true;
        SelectedNodeTitle = analysis.FilePath ?? analysis.DetectedFormat ?? "Analysis";

        if (!string.IsNullOrEmpty(analysis.DetectedFormat))
        {
            Properties.Add(new PropertyRow("Format", analysis.DetectedFormat));
        }

        foreach (var row in analysis.Analysis)
        {
            Properties.Add(new PropertyRow(row.Name, row.Value));
        }

        LogService.Instance.Write(LogLevel.Debug, "Properties", $"Refreshed for file analysis '{analysis.FilePath}' ({analysis.Analysis.Count} entries)");
    }

    /// <summary>
    /// <para>异步读取固件详细参数并追加到属性行。</para>
    /// Asynchronously reads firmware detailed parameters and appends them to the property rows.
    /// </summary>
    private async Task RefreshImageDetailsAsync(ImageEntryViewModel image)
    {
        try
        {
            var context = ProjectContextService.Instance;
            var absolutePath = context.IsProjectOpen ? context.ResolvePath(image.RelativePath) : null;
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
            {
                return;
            }

            var handler = FormatDetector.Detect(absolutePath);
            if (handler is null)
            {
                return;
            }

            var result = await handler.ReadMetadataAsync(absolutePath).ConfigureAwait(true);
            if (!result.Success)
            {
                return;
            }

            if (result.Metadata != null)
            {
                foreach (var kv in result.Metadata)
                {
                    Properties.Add(new PropertyRow(kv.Key, kv.Value));
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Debug, "Properties", $"Failed to read image details: {ex.Message}");
        }
    }
}

/// <summary>
/// <para>属性面板展示的内容类型。</para>
/// The type of content displayed in the properties panel.
/// </summary>
public enum PropertiesContentType
{
    /// <summary>
    /// <para>无内容。</para>
    /// No content.
    /// </summary>
    None,

    /// <summary>
    /// <para>节点属性。</para>
    /// Node properties.
    /// </summary>
    Node,

    /// <summary>
    /// <para>镜像参数。</para>
    /// Image parameters.
    /// </summary>
    Image,

    /// <summary>
    /// <para>文件分析结果。</para>
    /// File analysis results.
    /// </summary>
    FileAnalysis
}