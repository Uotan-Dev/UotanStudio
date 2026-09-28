using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Components;

internal sealed class BootComponentFiles(string directory, string outputPath, CancellationToken cancellationToken) : IDisposable
{
    private readonly Dictionary<string, FileStream?> _inputs = [];

    internal void Add(string key, string filename, uint originalSize)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = FileExtraction.ResolvePath(directory, filename);
        if (Path.GetFullPath(outputPath).Equals(path, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Boot output must not replace a component.");
        if (!File.Exists(path))
        {
            if (originalSize != 0) throw new InvalidDataException($"Missing boot component: {filename}.");
            _inputs.Add(key, null);
            return;
        }
        var input = File.OpenRead(path);
        _inputs.Add(key, input);
        if (input.Length > uint.MaxValue) throw new InvalidDataException("Boot component exceeds its 32-bit size field.");
    }

    internal long Length(string key) => _inputs[key]?.Length ?? 0;

    internal void Copy(string key, Stream output)
    {
        if (_inputs[key] is not { } input) return;
        input.Position = 0;
        StreamCopy.CopyExactly(input, output, input.Length, cancellationToken);
    }

    internal void AppendHash(string key, IncrementalHash hash)
    {
        if (_inputs[key] is { } input)
        {
            input.Position = 0;
            var buffer = new byte[65536];
            var remaining = input.Length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(remaining, buffer.Length);
                input.ReadExactly(buffer.AsSpan(0, count));
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
        }
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)Length(key)));
        hash.AppendData(size);
    }

    public void Dispose()
    {
        foreach (var input in _inputs.Values) input?.Dispose();
    }
}