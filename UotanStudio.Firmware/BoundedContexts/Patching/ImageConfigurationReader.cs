using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.Patching;

/// <summary>Extracts one bounded regular configuration file from raw EXT4/EROFS without host path traversal.</summary>
public static class ImageConfigurationReader
{
    /// <summary>Streams at most 16 MiB into output, leaving caller streams open; rejects links and special files.</summary>
    public static void Extract(Stream image, string format, string path, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (path.Length is < 1 or > 4096 || path.Contains('\0') || path.Contains('\\'))
        {
            throw new InvalidDataException("Invalid image-relative file path.");
        }
        path = path.TrimStart('/');
        if (path.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Image path must identify one literal file without traversal.");
        }
        using var source = new BoundedReadStream(image, 0, image.Length, leaveOpen: true);
        using var limited = new LimitedOutput(output, token);
        long size;
        switch (format)
        {
            case "EXT4":
                using (var parser = new Ext4Parser(source, 100000, 16 * 1024 * 1024, token))
                {
                    var entry = parser.ListFiles().SingleOrDefault(entry => entry.Path == path)
                        ?? throw new FileNotFoundException("Image configuration file is missing.", path);
                    Validate(entry.Mode, entry.Size);
                    size = entry.Size;
                    parser.ReadFile(path, limited);
                }
                break;
            case "EROFS":
                using (var parser = new ErofsParser(source, 100000, 16 * 1024 * 1024, token))
                {
                    var entry = parser.EnumerateFiles().SingleOrDefault(entry => entry.Path == path)
                        ?? throw new FileNotFoundException("Image configuration file is missing.", path);
                    Validate(entry.Mode, entry.Size);
                    size = entry.Size;
                    parser.ReadFile(path, limited);
                }
                break;
            default:
                throw new InvalidDataException("Only raw EXT4 and EROFS image configuration reads are supported.");
        }
        token.ThrowIfCancellationRequested();
        if (limited.Written != size) { throw new InvalidDataException("Image file output does not match its declared size."); }
    }

    private static void Validate(ushort mode, long size)
    {
        if ((mode & 0xF000) != 0x8000 || size is < 0 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Image configuration must be a regular file of at most 16 MiB.");
        }
    }

    private sealed class LimitedOutput(Stream output, CancellationToken token) : Stream
    {
        internal long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Length > 16 * 1024 * 1024 - Written) { throw new InvalidDataException("Image configuration exceeds the output limit."); }
            output.Write(buffer);
            Written += buffer.Length;
        }
    }
}