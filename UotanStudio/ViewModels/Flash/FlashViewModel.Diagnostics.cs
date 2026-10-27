using System.IO.Ports;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Diag;
using UotanStudio.Comm.Protocols.Edl.Crash;
using UotanStudio.Comm.Protocols.Edl.FlashBundle.FactoryBuild;
using UotanStudio.Comm.Transports.Serial;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Ui;

namespace UotanStudio.ViewModels.Flash;

public sealed partial class FlashViewModel
{
    /// <summary>
    /// NV item ids the QCN backup reads. The set is intentionally small and read-only.
    /// <para>QCN 备份读取的 NV 条目号。集合刻意保持小而只读。</para>
    /// </summary>
    private static readonly ushort[] QcnBackupTemplateItems = [10, 187, 4471, 550, 551, 653];

    private DiagSession OpenDiagSession(string portName)
    {
        var transport = new SerialTransport(portName);
        transport.Connect();
        return new DiagSession(transport);
    }

    private string ReadDiagPortName()
    {
        var port = Field<TextBox>("DiagComPortBox").Text?.Trim();
        if (string.IsNullOrWhiteSpace(port))
        {
            throw new InvalidOperationException(LanguageService.Instance.GetString("Flash_DiagComPortWatermark"));
        }

        return port;
    }

    private async void RunDiagActionAsync(string titleKey, Func<DiagSession, CancellationToken, Task> action,
        bool needsConfirmation = false, string? confirmKey = null)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        var language = LanguageService.Instance;
        string portName;
        try
        {
            portName = ReadDiagPortName();
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            return;
        }

        if (needsConfirmation)
        {
            _confirming = true;
            bool confirmed;
            try
            {
                confirmed = await DialogService.ConfirmAsync(owner, language.GetString(titleKey),
                    language.GetString(confirmKey ?? titleKey));
            }
            finally
            {
                _confirming = false;
            }

            if (!confirmed)
            {
                return;
            }
        }

        SetBusy(true);
        try
        {
            using var session = OpenDiagSession(portName);
            await action(session, CancellationToken.None);
            AppendLog(string.Format(language.GetString("Flash_OkResult"), language.GetString(titleKey)));
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal void OnDiagReadNvClick(object? sender, RoutedEventArgs e)
        => RunDiagActionAsync("Flash_DiagReadNv", async (session, cancellationToken) =>
        {
            var item = await session.ReadNvItemAsync(10, cancellationToken);
            AppendLog($"NV 10: {Convert.ToHexString(item.Data)}");
        });

    internal void OnDiagBackupQcnClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        RunStorageTargetAsync("Flash_DiagBackupQcn", async (session, destination, cancellationToken) =>
        {
            var refused = await session.BackupQcnAsync(destination, QcnBackupTemplateItems,
                cancellationToken: cancellationToken);
            AppendLog(refused.Count == 0
                ? LanguageService.Instance.GetString("Flash_DiagQcnBackupDone")
                : $"{LanguageService.Instance.GetString("Flash_DiagQcnBackupDone")} ({refused.Count})");
        });
    }

    internal void OnDiagRestoreQcnClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        RunDiagActionAsync("Flash_DiagRestoreQcn", async (session, cancellationToken) =>
        {
            var paths = await TopLevel.GetTopLevel(Surface)!.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = LanguageService.Instance.GetString("Flash_DiagRestoreQcn"),
                AllowMultiple = false,
                FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("QCN")
                {
                    Patterns = ["*.qcn"],
                }],
            });
            if (paths.Count == 0)
            {
                return;
            }

            var refused = await session.RestoreQcnAsync(paths[0].Path.LocalPath, confirm: true,
                cancellationToken: cancellationToken);
            AppendLog($"{LanguageService.Instance.GetString("Flash_DiagQcnRestoreDone")} ({refused.Count})");
        }, needsConfirmation: true, confirmKey: "Flash_DiagRestoreConfirm");
    }

    private async void RunStorageTargetAsync(string titleKey,
        Func<DiagSession, string, CancellationToken, Task> action)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(Surface);
        if (topLevel is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = LanguageService.Instance.GetString(titleKey),
        });
        if (folders.Count == 0)
        {
            return;
        }

        var language = LanguageService.Instance;
        string portName;
        try
        {
            portName = ReadDiagPortName();
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            return;
        }

        SetBusy(true);
        try
        {
            using var session = OpenDiagSession(portName);
            await action(session, folders[0].Path.LocalPath, CancellationToken.None);
            AppendLog(string.Format(language.GetString("Flash_OkResult"), language.GetString(titleKey)));
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal void OnDiagListEfsClick(object? sender, RoutedEventArgs e)
        => RunDiagActionAsync("Flash_DiagListEfs", async (session, cancellationToken) =>
        {
            var entries = await session.ListEfsDirectoryAsync("/", cancellationToken);
            foreach (var entry in entries.Take(64))
            {
                AppendLog($"{(entry.IsDirectory ? "d" : "-")} {entry.Name}");
            }
        });

    internal void OnDiagSyncStatusClick(object? sender, RoutedEventArgs e)
        => RunDiagActionAsync("Flash_DiagSyncStatus", async (session, cancellationToken) =>
        {
            var status = await session.ReadEfsSyncStatusAsync(cancellationToken);
            AppendLog($"EFS sync: {status}");
        });

    internal void OnCrashDumpClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || SelectedDevice is not { } device ||
            TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        if (device.Protocol != DeviceProtocolKind.Edl)
        {
            AppendLog(LanguageService.Instance.GetString("Flash_ModeMismatch"));
            return;
        }

        AppendLog(LanguageService.Instance.GetString("Flash_CrashDumpHint"));
    }

    internal void OnFactoryBuildFlattenClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _confirming || TopLevel.GetTopLevel(Surface) is not Window owner)
        {
            return;
        }

        AppendLog(LanguageService.Instance.GetString("Flash_FactoryBuildHint"));
    }
}