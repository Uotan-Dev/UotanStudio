using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Core;
using UotanStudio.ViewModels;
using UotanStudio.ViewModels.Editors;
using Ursa.Controls;

namespace UotanStudio.Services.Workspace;

/// <summary>Shares explicit document saving and close confirmation. 复用文档手动保存与关闭确认。</summary>
internal static class DocumentSaveService
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    internal static bool CanSave(object? document) => document is FileViewModel or HexEditorViewModel
        or DtbEditorViewModel or ManifestEditorViewModel or PartitionEditorViewModel;

    internal static bool IsDirty(object? document) => document switch
    {
        FileViewModel text => text.IsDirty,
        HexEditorViewModel hex => hex.IsDirty,
        DtbEditorViewModel dtb => dtb.IsDirty,
        ManifestEditorViewModel manifest => manifest.IsDirty,
        PartitionEditorViewModel partition => partition.IsDirty,
        _ => false
    };

    internal static async Task<bool> SaveAsync(object document, Window? owner = null)
    {
        owner ??= DialogService.GetOwnerWindow();
        await SaveGate.WaitAsync();
        try
        {
            if (document is FileViewModel text)
            {
                if (string.IsNullOrWhiteSpace(text.FilePath))
                {
                    if (owner is null) return false;
                    var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = LanguageService.Instance.GetString("Menu_SaveAs"),
                        SuggestedFileName = text.Title
                    });
                    if (file?.TryGetLocalPath() is not { } path) return false;
                    await text.SaveAsAsync(path);
                }
                else await text.SaveAsync();
            }
            else
            {
                IAsyncRelayCommand? command = document switch
                {
                    HexEditorViewModel hex => hex.SaveCommand,
                    DtbEditorViewModel dtb => dtb.SaveCommand,
                    ManifestEditorViewModel manifest => manifest.SaveCommand,
                    PartitionEditorViewModel partition => partition.SaveCommand,
                    _ => null
                };
                if (command is null) return false;
                if (command.IsRunning && command.ExecutionTask is { } pending) await pending;
                else if (command.CanExecute(null)) await command.ExecuteAsync(null);
            }

            var error = document switch
            {
                FileViewModel textDocument => textDocument.CompileError,
                HexEditorViewModel hex => hex.CompileError,
                DtbEditorViewModel dtb => dtb.CompileError,
                ManifestEditorViewModel manifest => manifest.CompileError,
                PartitionEditorViewModel partition => partition.CompileError,
                _ => string.Empty
            };
            if (!string.IsNullOrEmpty(error) || IsDirty(document))
                throw new IOException(string.IsNullOrEmpty(error)
                    ? LanguageService.Instance.GetString("Dialog_SaveIncomplete") : error);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "Editor", ex.Message);
            if (owner is not null && owner.IsVisible)
                await DialogService.ShowAsync(owner, LanguageService.Instance.GetString("Dialog_SaveFailed"), ex.Message, MessageBoxIcon.Error);
            return false;
        }
        finally { SaveGate.Release(); }
    }

    internal static async Task<bool> ConfirmCloseAsync(IDockable document, Window? owner = null)
    {
        if (!IsDirty(document)) return true;
        owner ??= DialogService.GetOwnerWindow();
        if (owner is null) return false;
        var result = await DialogService.SaveChangesAsync(owner, document.Title ?? string.Empty);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && await SaveAsync(document, owner);
    }

    internal static async Task<bool> ConfirmAllAsync(IEnumerable<IDockable> documents, Window? owner = null)
    {
        foreach (var document in documents.Where(IsDirty).Distinct().ToArray())
            if (!await ConfirmCloseAsync(document, owner)) return false;
        return true;
    }
}