using System.Diagnostics;
using Avalonia.Threading;
using UotanStudio.Services;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Audio;

namespace UotanStudio.ViewModels.Workspace;

internal partial class PropertiesViewModel
{
    private ScreenMirrorViewModel? _mirror;
    public ScreenMirrorViewModel? ActiveMirror => _mirror;
    public bool IsMirrorSelected => _mirror is not null;
    private DispatcherTimer? _mirrorTimer;
    private long _mirrorSampleTime;
    private (long Received, long Decoded, long Uploaded, long Bytes) _mirrorCounters;
    private long _mirrorAudioFrames;
    private static readonly string[] MirrorKeys = ["Status", "Codec", "Decoder", "Resolution", "SourceColor", "PixelFormat", "Conversion", "ReceiveFps", "DecodeFps", "UploadFps", "MaxFps", "BitRate", "ActualBitRate", "Encoder", "Audio", "AudioFormat", "AudioFps", "Device", "Version"];

    internal void RefreshForScreenMirror(ScreenMirrorViewModel mirror)
    {
        if (_mirror == mirror && ContentType == PropertiesContentType.ScreenMirror) return;
        ClearProperties();
        _mirror = mirror;
        OnPropertyChanged(nameof(ActiveMirror));
        OnPropertyChanged(nameof(IsMirrorSelected));
        ContentType = PropertiesContentType.ScreenMirror;
        HasSelection = true;
        SelectedNodeTitle = mirror.Title;
        foreach (string key in MirrorKeys) Properties.Add(new PropertyRow(LanguageService.Instance.GetString("MirrorProperty_" + key), "—"));
        AddMirrorSelectors(mirror);
        _mirrorCounters = mirror.ReadCounters();
        _mirrorSampleTime = Stopwatch.GetTimestamp();
        UpdateMirrorProperties(false);
        _mirrorTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateMirrorProperties(true));
        _mirrorTimer.Start();
    }

    partial void OnContentTypeChanged(PropertiesContentType value)
    {
        if (value != PropertiesContentType.ScreenMirror) StopMirrorUpdates();
    }

    private void StopMirrorUpdates()
    {
        _mirrorTimer?.Stop();
        _mirror = null;
        OnPropertyChanged(nameof(ActiveMirror));
        OnPropertyChanged(nameof(IsMirrorSelected));
    }

    private void UpdateMirrorProperties(bool sample)
    {
        if (_mirror is not { } mirror || ContentType != PropertiesContentType.ScreenMirror) return;
        var options = mirror.StreamOptions;
        var meta = mirror.StreamMetadata;
        Avalonia.PixelSize? size = mirror.VideoPixelSize is { Width: > 0 } pixels ? pixels : null;
        var svc = LanguageService.Instance;
        string unknown = svc.GetString("MirrorProperty_Unknown");
        string waiting = "—";
        string received = waiting, decoded = waiting, uploaded = waiting, bitrate = waiting;
        double seconds = 0;
        if (sample)
        {
            long now = Stopwatch.GetTimestamp();
            seconds = (now - _mirrorSampleTime) / (double)Stopwatch.Frequency;
            var counters = mirror.ReadCounters();
            if (seconds > 0)
            {
                received = $"{Math.Max(0, counters.Received - _mirrorCounters.Received) / seconds:F1} fps";
                decoded = $"{Math.Max(0, counters.Decoded - _mirrorCounters.Decoded) / seconds:F1} fps";
                uploaded = $"{Math.Max(0, counters.Uploaded - _mirrorCounters.Uploaded) / seconds:F1} fps";
                bitrate = $"{Math.Max(0, counters.Bytes - _mirrorCounters.Bytes) * 8 / seconds / 1_000_000:F2} Mbps";
            }
            _mirrorCounters = counters;
            _mirrorSampleTime = now;
        }
        string audioValue;
        if (!mirror.AudioEnabled)
        {
            audioValue = svc.GetString("MirrorProperty_AudioOff");
        }
        else
        {
            var audioMeta = mirror.StreamMetadataAudio;
            audioValue = mirror.AudioDisabledByServer ? svc.GetString("MirrorProperty_AudioUnavailable") : audioMeta?.CodecName.ToUpperInvariant() ?? waiting;
        }
        int audioRate = mirror.AudioSampleRate;
        int audioChannels = mirror.AudioChannels;
        string audioFormat = !mirror.AudioEnabled ? waiting
            : audioRate > 0 ? $"{audioRate} Hz · {audioChannels} ch" : waiting;
        string audioFps = !mirror.AudioEnabled ? waiting
            : sample && audioRate > 0
                ? seconds > 0 ? $"{Math.Max(0, mirror.AudioDecodedFrames - _mirrorAudioFrames) / seconds:F1} fps" : waiting
                : waiting;
        if (sample) _mirrorAudioFrames = mirror.AudioDecodedFrames;
        string[] values = [mirror.StatusText, meta?.CodecName.ToUpperInvariant() ?? options?.VideoCodec.ToUpperInvariant() ?? waiting,
            string.IsNullOrEmpty(mirror.DecoderBackend) ? waiting : mirror.DecoderBackend,
            size is { } s ? $"{s.Width} × {s.Height}" : meta is { } m ? $"{m.Width} × {m.Height}" : waiting,
            svc.GetString("MirrorProperty_Unknown"),
            mirror.DecoderBackend == "Media Foundation (GPU)" ? "BGRA · 8-bit" : "NV12 · 8-bit · YUV 4:2:0",
            mirror.DecoderBackend == "Media Foundation (GPU)" ? "D3D11 → BGRA" : svc.GetString("MirrorProperty_ConversionValue"), received, decoded, uploaded,
            options is null ? waiting : options.MaxFps > 0 ? $"{options.MaxFps} fps" : svc.GetString("MirrorProperty_Unlimited"),
            options is null ? waiting : $"{options.VideoBitRate / 1_000_000.0:F2} Mbps", bitrate,
            svc.GetString("MirrorProperty_EncoderValue"), audioValue, string.IsNullOrEmpty(mirror.AudioBackend) ? audioFormat : $"{audioFormat} ? {mirror.AudioBackend}", audioFps,
            mirror.DeviceName, UotanStudio.Comm.Protocols.Scrcpy.ScrcpyConstants.ServerVersion];
        for (int i = 0; i < values.Length; i++)
        {
            Properties[i].Name = svc.GetString("MirrorProperty_" + MirrorKeys[i]);
            Properties[i].Value = values[i];
        }
        if (mirror.IsStopped) StopMirrorUpdates();
    }

    private void AddMirrorSelectors(ScreenMirrorViewModel mirror)
    {
        var settings = mirror.PendingSettings;
        var svc = LanguageService.Instance;
        string L(string key) => svc.GetString("MirrorSetting_" + key);
        PropertyChoice C(string value, string label) => new(value, label);
        void Add(string key, string selected, IReadOnlyList<PropertyChoice> choices, Action<string> update)
        {
            if (!choices.Any(c => c.Value == selected)) choices = [.. choices, C(selected, selected)];
            var row = new PropertyRow(L(key), "") { SettingKey = "MirrorSetting_" + key, Choices = choices, SelectedChoice = choices.FirstOrDefault(c => c.Value == selected) };
            row.ChoiceChanged = value => { update(value); mirror.MarkMirrorSettingsChanged(); };
            Properties.Add(row);
        }
        Add("VideoCodec", settings.ScreenMirrorVideoCodec, [C("h264", "H.264"), C("h265", "H.265 / HEVC"), C("av1", "AV1")], v => settings.ScreenMirrorVideoCodec = v);
        var backends = new List<PropertyChoice> { C("0", L("Auto")) };
        if (OperatingSystem.IsWindows()) backends.AddRange([C("1", "Media Foundation · GPU"), C("2", "Media Foundation · CPU")]);
        if (OperatingSystem.IsMacOS()) backends.Add(C("3", "VideoToolbox · GPU"));
        backends.AddRange([C("4", "GStreamer · GPU"), C("5", "GStreamer · CPU")]);
        Add("Backend", ((int)settings.ScreenMirrorDecoderBackend).ToString(), backends, v => settings.ScreenMirrorDecoderBackend = (VideoDecoderBackend)int.Parse(v));
        Add("Matrix", ((int)settings.ScreenMirrorColorMatrix).ToString(), [C("0", L("Auto")), C("1", "BT.601"), C("2", "BT.709")], v => settings.ScreenMirrorColorMatrix = (VideoColorMatrix)int.Parse(v));
        Add("Range", ((int)settings.ScreenMirrorColorRange).ToString(), [C("0", L("Auto")), C("1", L("Limited")), C("2", L("Full"))], v => settings.ScreenMirrorColorRange = (VideoColorRange)int.Parse(v));
        Add("MaxFps", settings.ScreenMirrorMaxFps.ToString(), [C("0", L("Unlimited")), C("30", "30 fps"), C("60", "60 fps"), C("90", "90 fps"), C("120", "120 fps")], v => settings.ScreenMirrorMaxFps = int.Parse(v));
        Add("MaxSize", settings.ScreenMirrorMaxSize.ToString(), [C("0", L("Original")), C("720", "720 px"), C("1080", "1080 px"), C("1600", "1600 px"), C("1920", "1920 px"), C("2560", "2560 px")], v => settings.ScreenMirrorMaxSize = int.Parse(v));
        Add("BitRate", settings.ScreenMirrorBitRate.ToString(), [C("2000000", "2 Mbps"), C("4000000", "4 Mbps"), C("8000000", "8 Mbps"), C("16000000", "16 Mbps"), C("32000000", "32 Mbps")], v => settings.ScreenMirrorBitRate = int.Parse(v));
        Add("AudioCodec", settings.ScreenMirrorAudioEnabled ? settings.ScreenMirrorAudioCodec : "off", [C("off", L("Off")), C("aac", "AAC"), C("opus", "Opus"), C("flac", "FLAC"), C("raw", "PCM")], v => { settings.ScreenMirrorAudioEnabled = v != "off"; if (v != "off") settings.ScreenMirrorAudioCodec = v; });
        Add("AudioBackend", ((int)settings.ScreenMirrorAudioBackend).ToString(), [C("0", L("Auto")), C("1", L("Native")), C("2", "GStreamer")], v => settings.ScreenMirrorAudioBackend = (AudioDecoderBackend)int.Parse(v));
        Add("Volume", ((int)(settings.ScreenMirrorVolume * 100)).ToString(), [C("0", "0%"), C("25", "25%"), C("50", "50%"), C("75", "75%"), C("100", "100%")], v => settings.ScreenMirrorVolume = int.Parse(v) / 100f);
        Add("Control", settings.ScreenMirrorControl ? "on" : "off", [C("on", L("On")), C("off", L("Off"))], v => settings.ScreenMirrorControl = v == "on");
    }
}
