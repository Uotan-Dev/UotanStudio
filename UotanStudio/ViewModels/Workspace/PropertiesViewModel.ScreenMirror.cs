using System.Diagnostics;
using Avalonia.Threading;
using UotanStudio.Services;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.ViewModels.Workspace;

internal partial class PropertiesViewModel
{
    private ScreenMirrorViewModel? _mirror;
    private DispatcherTimer? _mirrorTimer;
    private long _mirrorSampleTime;
    private (long Received, long Decoded, long Uploaded, long Bytes) _mirrorCounters;
    private static readonly string[] MirrorKeys = ["Status", "Codec", "Decoder", "Resolution", "SourceColor", "PixelFormat", "Conversion", "ReceiveFps", "DecodeFps", "UploadFps", "MaxFps", "BitRate", "ActualBitRate", "Encoder", "Device", "Version"];

    internal void RefreshForScreenMirror(ScreenMirrorViewModel mirror)
    {
        if (_mirror == mirror && ContentType == PropertiesContentType.ScreenMirror) return;
        ClearProperties();
        _mirror = mirror;
        ContentType = PropertiesContentType.ScreenMirror;
        HasSelection = true;
        SelectedNodeTitle = mirror.Title;
        foreach (string key in MirrorKeys) Properties.Add(new PropertyRow(LanguageService.Instance.GetString("MirrorProperty_" + key), "—"));
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
    }

    private void UpdateMirrorProperties(bool sample)
    {
        if (_mirror is not { } mirror || ContentType != PropertiesContentType.ScreenMirror) return;
        var options = mirror.StreamOptions;
        var meta = mirror.StreamMetadata;
        var size = mirror.Frame?.PixelSize;
        var svc = LanguageService.Instance;
        string unknown = svc.GetString("MirrorProperty_Unknown");
        string waiting = "—";
        string received = waiting, decoded = waiting, uploaded = waiting, bitrate = waiting;
        if (sample)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - _mirrorSampleTime) / (double)Stopwatch.Frequency;
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
        string[] values = [mirror.StatusText, meta?.CodecName.ToUpperInvariant() ?? options?.VideoCodec.ToUpperInvariant() ?? waiting,
            string.IsNullOrEmpty(mirror.DecoderBackend) ? waiting : mirror.DecoderBackend,
            size is { } s ? $"{s.Width} × {s.Height}" : meta is { } m ? $"{m.Width} × {m.Height}" : waiting,
            unknown, "NV12 · 8-bit · YUV 4:2:0", svc.GetString("MirrorProperty_ConversionValue"), received, decoded, uploaded,
            options is null ? waiting : options.MaxFps > 0 ? $"{options.MaxFps} fps" : svc.GetString("MirrorProperty_Unlimited"),
            options is null ? waiting : $"{options.VideoBitRate / 1_000_000.0:F2} Mbps", bitrate,
            svc.GetString("MirrorProperty_EncoderValue"), mirror.DeviceName, UotanStudio.Comm.Protocols.Scrcpy.ScrcpyConstants.ServerVersion];
        for (int i = 0; i < values.Length; i++)
        {
            Properties[i].Name = svc.GetString("MirrorProperty_" + MirrorKeys[i]);
            Properties[i].Value = values[i];
        }
        if (mirror.IsStopped) StopMirrorUpdates();
    }
}
