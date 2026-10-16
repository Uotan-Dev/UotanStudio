using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Media.Decoding;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

public partial class ScreenMirrorViewModel
{
    private string? _serial;
    private Task? _startTask;
    private AppSettings? _pendingSettings;
    private AppSettings _activeSettings = new();
    internal AppSettings PendingSettings => _pendingSettings ??= SettingsPersistenceService.Instance.LoadSettings().Copy();
    /// <summary>Decoder options for the active session.<para>当前会话的解码器选项。</para></summary>
    /// <value>Decoder options derived from the active settings.<para>依据当前生效设置得出的解码器选项。</para></value>
    internal VideoDecoderOptions ActiveDecoderOptions
    {
        get
        {
            var composition = GpuPresenter.Composition;
            var windowsExport = OperatingSystem.IsWindows() && composition is { WindowsKeyedMutex: true }
                && _activeSettings.ScreenMirrorGpuExport != GpuExportPreference.Off;
            var macExport = OperatingSystem.IsMacOS() && composition is { MacIosurface: true }
                && _activeSettings.ScreenMirrorGpuExport != GpuExportPreference.Off;
            return new VideoDecoderOptions
            {
                Backend = _activeSettings.ScreenMirrorDecoderBackend,
                ColorMatrix = _activeSettings.ScreenMirrorColorMatrix,
                ColorRange = _activeSettings.ScreenMirrorColorRange,
                PreferNv12Output = true,
                MediaFoundationPreferGpuExport = windowsExport,
                MediaFoundationAdapterLuid = windowsExport ? composition!.AdapterLuid : null,
                VideoToolboxPreferGpuExport = macExport,
            };
        }
    }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyMirrorSettingsCommand))]
    private bool _isApplyingMirrorSettings;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateMirrorServerCommand))]
    private bool _isUpdatingMirrorServer;
    [ObservableProperty]
    private bool _hasPendingMirrorSettings;
    internal void MarkMirrorSettingsChanged() => HasPendingMirrorSettings = true;
    private bool CanApplyMirrorSettings() => !IsApplyingMirrorSettings && !_disposed && _serial is not null;
    private bool CanUpdateMirrorServer() => !IsUpdatingMirrorServer && !_disposed;

    /// <summary>
    /// Fetches the newest published scrcpy server and installs it for the next session.
    /// <para>获取最新发布的 scrcpy 服务端，并安装供下一次会话使用。</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A running session keeps the jar it already deployed: the version is fixed when the arguments are
    /// built, so the new artifact takes effect on the next connect.
    /// </para>
    /// <para>
    /// 已开始的会话仍使用它已经部署的 jar：版本在构建参数时即已确定，因此新产物从下一次连接生效。
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanUpdateMirrorServer))]
    private async Task UpdateMirrorServerAsync()
    {
        IsUpdatingMirrorServer = true;
        var previous = _streamOptions?.ServerVersion ?? Services.Scrcpy.ScrcpyServerService.BundledVersion;
        try
        {
            var artifact = await Services.Scrcpy.ScrcpyServerService.Shared
                .EnsureLatestAsync(_cts?.Token ?? CancellationToken.None);
            StatusText = string.Equals(artifact.Version, previous, StringComparison.Ordinal)
                ? $"scrcpy server {artifact.Version} ({artifact.Source})"
                : $"scrcpy server {previous} -> {artifact.Version} ({artifact.Source})";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            StatusText = error.Message;
            LogService.Instance.Write(LogLevel.Warn, "ScreenMirror",
                $"scrcpy server update failed: {error.Message}");
        }
        finally
        {
            IsUpdatingMirrorServer = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyMirrorSettings))]
    private async Task ApplyMirrorSettingsAsync()
    {
        IsApplyingMirrorSettings = true;
        try
        {
            if (_startTask is { } start) await start;
            _cts?.Cancel();
            _client?.Dispose();
            await Task.Run(() =>
            {
                if (_decodeThread is { IsAlive: true } video && !video.Join(6000)) throw new TimeoutException("Video decoder did not stop; reconnect was cancelled.");
                if (_audioThread is { IsAlive: true } audio && !audio.Join(6000)) throw new TimeoutException("Audio output did not stop; reconnect was cancelled.");
            });
            if (_disposed) return;
            Cleanup();
            GpuPresenter = new();
            _disposed = false;
            FramePresented?.Invoke(this, EventArgs.Empty);
            _decodeThread = _audioThread = null;
            _decoderBackend = string.Empty;
            _receivedFrames = _decodedFrames = _uploadedFrames = _receivedBytes = _audioDecodedFrames = 0;
            _audioSampleRate = _audioChannels = 0;
            var latest = SettingsPersistenceService.Instance.LoadSettings();
            CopyMirrorSettings(PendingSettings, latest);
            if (!SettingsPersistenceService.Instance.TrySaveSettings(latest, out var saveError))
                throw new IOException(saveError ?? "Unable to save mirroring settings.");
            HasPendingMirrorSettings = false;
            StartForDevice(_serial!);
        }
        catch (Exception error)
        {
            StatusText = GetLocalizedString("ScreenMirror_Failed") + " " + UserError.Describe(error);
            LogService.Instance.Write(LogLevel.Error, "ScreenMirror", error.Message);
        }
        finally { IsApplyingMirrorSettings = false; }
    }
    internal static void CopyMirrorSettings(AppSettings source, AppSettings target)
    {
        target.ScreenMirrorBitRate = source.ScreenMirrorBitRate;
        target.ScreenMirrorMaxFps = source.ScreenMirrorMaxFps;
        target.ScreenMirrorMaxSize = source.ScreenMirrorMaxSize;
        target.ScreenMirrorVideoCodec = source.ScreenMirrorVideoCodec;
        target.ScreenMirrorAudioEnabled = source.ScreenMirrorAudioEnabled;
        target.ScreenMirrorDecoderBackend = source.ScreenMirrorDecoderBackend;
        target.ScreenMirrorColorMatrix = source.ScreenMirrorColorMatrix;
        target.ScreenMirrorColorRange = source.ScreenMirrorColorRange;
        target.ScreenMirrorAudioCodec = source.ScreenMirrorAudioCodec;
        target.ScreenMirrorAudioBackend = source.ScreenMirrorAudioBackend;
        target.ScreenMirrorVolume = source.ScreenMirrorVolume;
        target.ScreenMirrorControl = source.ScreenMirrorControl;
        target.ScreenMirrorGpuExport = source.ScreenMirrorGpuExport;
    }
}