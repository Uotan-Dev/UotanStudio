using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Scrcpy;

namespace UotanStudio.Services.Devices;

/// <summary>Owns the ADB and scrcpy transports while presentation consumes decoded packets.</summary>
public sealed class ManagedMirrorSession(ScrcpyClient client, Stream shell, AdbSession? adbSession) : IDisposable
{
    private readonly StringBuilder _output = new();
    private int _disposed;

    public string DeviceName => client.DeviceName;
    public ScrcpyVideoMetadata? VideoMetadata => client.VideoMetadata;
    public ScrcpyAudioMetadata? AudioMetadata => client.AudioMetadata;
    public bool AudioDisabledByServer => client.AudioDisabledByServer;
    public bool IsControlConnected => client.IsControlConnected;

    public void Connect()
    {
        _ = Task.Run(DrainServerOutput);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    client.Connect(3000);
                    return;
                }
                catch (Exception) when (attempt < 19 && Volatile.Read(ref _disposed) == 0)
                {
                    Thread.Sleep(250);
                }
            }
        }
        catch (Exception error)
        {
            string detail;
            lock (_output) detail = _output.ToString().Trim();
            if (detail.Length == 0) throw;
            var summary = string.Join("\n", detail.Split('\n')
                .Where(line => line.Contains("ERROR:", StringComparison.Ordinal) &&
                    !line.Contains("Exception on thread", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith("--video-", StringComparison.Ordinal))
                .Take(6));
            throw new IOException(summary.Length > 0 ? summary : error.Message, error);
        }
    }

    public ScrcpyVideoFrame? ReadFrame(int timeoutMs) => client.ReadFrame(timeoutMs);
    public ScrcpyAudioFrame? ReadAudioFrame(int timeoutMs) => client.ReadAudioFrame(timeoutMs);
    public void SendControl(byte[] message) => client.SendControl(message);

    private void DrainServerOutput()
    {
        try
        {
            byte[] buffer = new byte[4096];
            while (Volatile.Read(ref _disposed) == 0)
            {
                int count;
                try { count = shell.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { continue; }
                catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException
                    { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut }) { continue; }
                if (count == 0) break;
                var text = Encoding.UTF8.GetString(buffer, 0, count);
                lock (_output)
                {
                    _output.Append(text);
                    if (_output.Length > 4096) _output.Remove(0, _output.Length - 4096);
                }
                LogService.Instance.Write(LogLevel.Debug, "ScreenMirror", text);
            }
        }
        catch (Exception) { /* Closing the session interrupts the shell reader. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        client.Dispose();
        shell.Dispose();
        adbSession?.Dispose();
    }
}
