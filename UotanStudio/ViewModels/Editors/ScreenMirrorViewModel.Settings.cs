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
    internal VideoDecoderOptions ActiveDecoderOptions => new()
    {
        Backend = _activeSettings.ScreenMirrorDecoderBackend,
        ColorMatrix = _activeSettings.ScreenMirrorColorMatrix,
        ColorRange = _activeSettings.ScreenMirrorColorRange,
    };
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyMirrorSettingsCommand))]
    private bool _isApplyingMirrorSettings;
    [ObservableProperty]
    private bool _hasPendingMirrorSettings;
    internal void MarkMirrorSettingsChanged() => HasPendingMirrorSettings = true;
    private bool CanApplyMirrorSettings() => !IsApplyingMirrorSettings && !_disposed && _serial is not null;

    [RelayCommand(CanExecute = nameof(CanApplyMirrorSettings))]
    private async Task ApplyMirrorSettingsAsync()
    {
        IsApplyingMirrorSettings = true;
        try
        {
            // Join the accepted startup before tearing down its owned streams.
            if (_startTask is { } start) await start;
            _cts?.Cancel();
            _client?.Dispose();
            _shellStream?.Dispose();
            await Task.Run(() =>
            {
                if (_decodeThread is { IsAlive: true } video && !video.Join(6000)) throw new TimeoutException("Video decoder did not stop; reconnect was cancelled.");
                if (_audioThread is { IsAlive: true } audio && !audio.Join(6000)) throw new TimeoutException("Audio output did not stop; reconnect was cancelled.");
            });
            if (_disposed) return;
            Cleanup();
            _disposed = false;
            _decodeThread = _audioThread = null;
            _decoderBackend = string.Empty;
            _receivedFrames = _decodedFrames = _uploadedFrames = _receivedBytes = _audioDecodedFrames = 0;
            _audioSampleRate = _audioChannels = 0;
            var latest = SettingsPersistenceService.Instance.LoadSettings();
            CopyMirrorSettings(PendingSettings, latest);
            if (!SettingsPersistenceService.Instance.TrySaveSettings(latest, out string? saveError))
                throw new IOException(saveError ?? "Unable to save mirroring settings.");
            HasPendingMirrorSettings = false;
            StartForDevice(_serial!);
        }
        catch (Exception error)
        {
            StatusText = GetLocalizedString("ScreenMirror_Failed") + " " + error.Message;
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
    }
}
