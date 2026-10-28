using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Dock.Model.Core;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Ui;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Workspace;
using Ursa.Controls;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Adds file operations for document tabs whose registered document instance exposes a file path.
/// <para>为注册表中具有文件路径的文档标签添加文件操作。</para>
/// </summary>
public sealed class DocumentFileActionsMenuItem : MenuItem
{
    private readonly OpenAsMenuItem _openAsMenuItem = new();
    private readonly MenuItem _convertMenuItem;
    private string? _path;

    /// <summary>Uses the standard menu-item theme. <para>使用标准菜单项主题。</para></summary>
    protected override Type StyleKeyOverride => typeof(MenuItem);

    /// <summary>Creates the document-tab file operations menu. <para>创建文档标签文件操作菜单。</para></summary>
    public DocumentFileActionsMenuItem()
    {
        Bind(HeaderProperty, new DynamicResourceExtension("Explorer_FileOperations"));
        Icon = CreateIcon(AppIconKind.FileCog);
        _openAsMenuItem.Bind(HeaderProperty, new DynamicResourceExtension("Explorer_OpenAsMenu"));
        _openAsMenuItem.Icon = CreateIcon(AppIconKind.OpenAs);
        Items.Add(_openAsMenuItem);
        Items.Add(new Separator());

        _convertMenuItem = CreateItem("Explorer_ConvertFormat");
        _convertMenuItem.Icon = CreateIcon(AppIconKind.Convert);
        _convertMenuItem.Items.Add(CreateConversionItem("Explorer_ConvertSparse", ImageViewModel.ConvertSparse));
        _convertMenuItem.Items.Add(CreateConversionItem("Explorer_ConvertImgToDat", ImageViewModel.ConvertImgToDat));
        _convertMenuItem.Items.Add(CreateConversionItem("Explorer_ConvertImgToBr", ImageViewModel.ConvertImgToBr));
        Items.Add(_convertMenuItem);

        var checksum = CreateItem("Explorer_Checksum");
        checksum.Icon = CreateIcon(AppIconKind.Checksum);
        checksum.Click += OnChecksum;
        Items.Add(checksum);

        var unpack = CreateItem("Explorer_Unpack");
        unpack.Icon = CreateIcon(AppIconKind.Extract);
        unpack.Click += OnUnpack;
        Items.Add(unpack);

        var openInExplorer = CreateItem("Explorer_OpenInFileExplorer");
        openInExplorer.Icon = CreateIcon(AppIconKind.OpenFolder);
        openInExplorer.Click += OnOpenInFileExplorer;
        Items.Add(openInExplorer);
        DataContextChanged += (_, _) => RefreshForDataContext();
    }

    private void RefreshForDataContext()
    {
        var path = DataContext is IDockable dockable
            ? DocumentOpenRegistry.ByInstance(dockable)?.GetPath?.Invoke(dockable)
            : null;
        _path = !string.IsNullOrWhiteSpace(path) ? path : null;
        IsVisible = _path is not null;
        _openAsMenuItem.IsVisible = _path is not null;
        _convertMenuItem.IsVisible = _path is not null && ImageInputService.SupportsFormatConversion(_path);
        if (_path is not null)
        {
            _openAsMenuItem.RefreshForPath(_path);
        }
    }

    private MenuItem CreateConversionItem(string resourceKey, string direction)
    {
        var item = CreateItem(resourceKey);
        item.Icon = CreateIcon(AppIconKind.Convert);
        item.Click += async (_, _) => await ConvertAsync(direction);
        return item;
    }

    private static MenuItem CreateItem(string resourceKey)
    {
        var item = new MenuItem();
        item.Bind(HeaderProperty, new DynamicResourceExtension(resourceKey));
        return item;
    }

    private static AppIcon CreateIcon(AppIconKind kind)
        => new() { Kind = kind, Width = 16, Height = 16 };

    private async Task ConvertAsync(string direction)
    {
        if (string.IsNullOrWhiteSpace(_path) || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var language = LanguageService.Instance;
        try
        {
            var preview = ImageViewModel.Instance.GetFormatConversionPreview(_path, direction);
            var message = string.Format(language.GetString("Image_ConvertConfirm"),
                preview.Operation, string.Join("\n", preview.OutputPaths));
            if (!await DialogService.ConfirmAsync(owner, language.GetString("Image_ConvertTitle"), message))
            {
                return;
            }

            await ImageViewModel.Instance.RunExplorerFormatConversionAsync(_path, direction);
            await DialogService.ShowAsync(owner, language.GetString("Image_ConvertTitle"),
                ImageViewModel.Instance.StatusText, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "DocumentTab", ex.Message);
            await DialogService.ShowAsync(owner, language.GetString("Image_ConvertTitle"), UserError.Describe(ex),
                MessageBoxIcon.Error);
        }
    }

    private void OnChecksum(object? sender, RoutedEventArgs e)
    {
        if (_path is not null)
        {
            UiWindowService.Shared.ShowChecksumWindow(_path);
        }
    }

    private async void OnUnpack(object? sender, RoutedEventArgs e)
    {
        if (_path is not null)
        {
            await ImageViewModel.Instance.RunExplorerOperationAsync(_path, false);
        }
    }

    private void OnOpenInFileExplorer(object? sender, RoutedEventArgs e)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Explorer", $"Open in explorer failed: {ex.Message}");
        }
    }
}
