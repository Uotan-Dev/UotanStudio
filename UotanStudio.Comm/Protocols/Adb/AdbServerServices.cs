using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>Bounded ADB smart-socket service access through an already running local adb server.</summary>
public sealed class AdbServerServices(string serial, string host = "127.0.0.1", int port = 5037)
{
    /// <summary>Lists server-managed targets without selecting a device transport.</summary>
    public IReadOnlyList<(string Serial, string State)> ListTargets()
    {
        using var client = new TcpClient { NoDelay = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        client.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
        using var stream = client.GetStream();
        stream.ReadTimeout = 10000;
        stream.WriteTimeout = 10000;
        Request(stream, "host:devices-l");
        Span<byte> lengthBytes = stackalloc byte[4];
        stream.ReadExactly(lengthBytes);
        if (!int.TryParse(Encoding.ASCII.GetString(lengthBytes), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var length) || length is < 0 or > 1048576)
            throw new IOException("Invalid ADB device list length.");
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        var targets = new List<(string Serial, string State)>();
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.AsSpan().Trim();
            var serialLength = fields.IndexOfAny(' ', '\t');
            if (serialLength <= 0) continue;
            var serial = fields[..serialLength].ToString();
            fields = fields[serialLength..].TrimStart();
            var stateLength = fields.IndexOfAny(' ', '\t');
            var state = (stateLength < 0 ? fields : fields[..stateLength]).ToString();
            if (state.Length != 0)
                targets.Add((serial, state));
        }
        return targets;
    }

    /// <summary>Opens an owned byte stream to a service on the explicitly selected device.</summary>
    public Stream Open(string service)
    {
        var stream = OpenTransport();
        try { Request(stream, service); return stream; }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>Checks the selected target without opening a device service.</summary>
    public void ValidateTarget()
    {
        using var stream = OpenTransport();
    }

    private Stream OpenTransport()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            client.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            var stream = client.GetStream();
            stream.ReadTimeout = 10000;
            stream.WriteTimeout = 10000;
            Request(stream, "host:transport:" + serial);
            return stream;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Pushes a local file using the ADB sync protocol and checks the device's final status.</summary>
    public void Push(string localPath, string remotePath)
    {
        using Stream stream = Open("sync:");
        using var input = File.OpenRead(localPath);
        Send(stream, "SEND", Encoding.UTF8.GetBytes(remotePath + ",33188"));
        byte[] buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer)) != 0) Send(stream, "DATA", buffer.AsSpan(0, count));
        Span<byte> header = stackalloc byte[8];
        "DONE"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        stream.Write(header);
        stream.ReadExactly(header);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (header[..4].SequenceEqual("OKAY"u8) && length == 0) return;
        if (!header[..4].SequenceEqual("FAIL"u8) || length > 65536) throw new IOException("Invalid ADB sync response.");
        byte[] message = new byte[(int)length]; stream.ReadExactly(message);
        throw new IOException(Encoding.UTF8.GetString(message));
    }
    private static void Send(Stream stream, string id, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        Encoding.ASCII.GetBytes(id, header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);
        stream.Write(header); stream.Write(data);
    }
    private static void Request(Stream stream, string request)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(request);
        if (bytes.Length > 65535) throw new ArgumentOutOfRangeException(nameof(request));
        stream.Write(Encoding.ASCII.GetBytes(bytes.Length.ToString("X4", CultureInfo.InvariantCulture)));
        stream.Write(bytes);
        Span<byte> status = stackalloc byte[4]; stream.ReadExactly(status);
        if (status.SequenceEqual("OKAY"u8)) return;
        if (!status.SequenceEqual("FAIL"u8)) throw new IOException("Invalid ADB server status.");
        stream.ReadExactly(status);
        int length = int.Parse(Encoding.ASCII.GetString(status), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte[] message = new byte[length]; stream.ReadExactly(message);
        throw new IOException("ADB server: " + Encoding.UTF8.GetString(message));
    }
}

/// <summary>Preserves unread bytes when adapting ADB packet chunks to a byte-oriented stream.</summary>
public sealed class AdbServiceStream(AdbStream stream) : Stream
{
    private bool _disposed;
    private byte[]? _pending;
    private int _offset;
    /// <inheritdoc />
    public override int ReadTimeout { get; set; } = 10000;
    /// <inheritdoc />
    public override bool CanTimeout => true;
    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => true;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    /// <inheritdoc />
    public override int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;
        if (_pending is null)
        {
            _pending = stream.Read(ReadTimeout); _offset = 0;
            if (_pending is null)
            {
                if (stream.IsFaulted) throw new IOException("ADB stream faulted.", stream.Fault);
                if (!stream.IsClosed) throw new TimeoutException("ADB service read timed out.");
                return 0;
            }
        }
        int count = Math.Min(destination.Length, _pending.Length - _offset);
        _pending.AsSpan(_offset, count).CopyTo(destination);
        _offset += count;
        if (_offset == _pending.Length) _pending = null;
        return count;
    }
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer.AsSpan(offset, count).ToArray());
    /// <inheritdoc />
    public override void Flush() { }
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; stream.Dispose(); }
        base.Dispose(disposing);
    }
}