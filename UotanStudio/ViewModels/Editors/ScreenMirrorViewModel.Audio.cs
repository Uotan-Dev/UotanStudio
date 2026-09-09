using UotanStudio.Comm.Protocols.Scrcpy;
using UotanStudio.Media.Audio;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

public partial class ScreenMirrorViewModel
{
    private string _audioBackend = string.Empty;
    internal string AudioBackend => _audioBackend;
    private void AudioLoop(ScrcpyClient client, CancellationToken token)
    {
        IAudioDecoder? decoder = null;
        IAudioSink? sink = null;
        bool sinkFailed = false;
        int sinkBits = 0;
        try
        {
            AudioCodecId codec = client.AudioMetadata?.CodecName switch
            {
                "AAC" => AudioCodecId.Aac,
                "OPUS" => AudioCodecId.Opus,
                "FLAC" => AudioCodecId.Flac,
                "RAW" => AudioCodecId.Pcm,
                _ => throw new NotSupportedException("Unsupported scrcpy audio codec."),
            };
            void OnAudioFrame(DecodedAudioFrame frame)
            {
                if (token.IsCancellationRequested) return;
                Interlocked.Increment(ref _audioDecodedFrames);
                Interlocked.Exchange(ref _audioSampleRate, frame.SampleRate);
                Interlocked.Exchange(ref _audioChannels, frame.Channels);
                if (sink is not null && (sink.SampleRate != frame.SampleRate || sink.Channels != frame.Channels || sinkBits != frame.BitsPerSample))
                {
                    sink.Dispose(); sink = null; sinkFailed = false;
                }
                if (sink is null && !sinkFailed)
                {
                    try
                    {
                        sink = AudioSinkFactory.Create();
                        sink.Open(frame.Channels, frame.SampleRate, frame.BitsPerSample);
                        sinkBits = frame.BitsPerSample;
                        sink.Volume = _activeSettings.ScreenMirrorVolume;
                        AudioSinkOpened?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception error)
                    {
                        sink?.Dispose(); sink = null; sinkFailed = true;
                        LogService.Instance.Write(LogLevel.Warn, "ScreenMirror", $"Audio playback unavailable: {error.Message}");
                    }
                }
                if (sink is not null)
                {
                    try { sink.Write(frame.Span); }
                    catch (Exception error)
                    {
                        sink.Dispose(); sink = null; sinkFailed = true;
                        LogService.Instance.Write(LogLevel.Warn, "ScreenMirror", $"Audio playback stopped: {error.Message}");
                    }
                }
            }
            try
            {
                decoder = AudioDecoderFactory.Create(codec, _activeSettings.ScreenMirrorAudioBackend);
                _audioBackend = decoder switch
                {
                    MediaFoundationAacDecoder => "Media Foundation AAC",
                    AudioToolboxAacDecoder => "AudioToolbox AAC",
                    GStreamerAudioDecoder => "GStreamer",
                    _ => "PCM",
                };
                if (codec == AudioCodecId.Pcm) decoder.Initialize([], OnAudioFrame);
            }
            catch (Exception error) { LogService.Instance.Write(LogLevel.Warn, "ScreenMirror", error.Message); }
            while (!token.IsCancellationRequested)
            {
                var packet = client.ReadAudioFrame(1000);
                if (decoder is null) continue; // Keep draining even if decoding failed.
                try
                {
                    if (packet is null) { decoder.Poll(); continue; }
                    if (packet.IsCodecConfig)
                    {
                        decoder.Initialize(packet.Data, OnAudioFrame); // Preserve playback callback across codec configuration changes.
                        continue;
                    }
                    if (decoder.IsInitialized) decoder.Decode(packet.Data, checked(packet.PresentationTimestampMicroseconds * 10));
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    LogService.Instance.Write(LogLevel.Warn, "ScreenMirror", $"Audio decode stopped; draining transport: {error.Message}");
                    decoder.Dispose(); decoder = null;
                }
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            LogService.Instance.Write(LogLevel.Error, "ScreenMirror", $"Audio transport failed: {error.Message}");
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        finally { sink?.Dispose(); decoder?.Dispose(); }
    }
}