using System.Text;
using UotanStudio.Firmware.Payload.Exceptions;

namespace UotanStudio.Firmware.Payload.Core;

internal class PayloadHeader
{
    internal const ulong BrilloMajorPayloadVersion = 2UL;

    internal const int HeaderSize = 24;
    private const ulong MaximumManifestLength = 512UL * 1024 * 1024;
    private const uint MaximumMetadataSignatureLength = 64U * 1024 * 1024;


    private static readonly HashSet<uint> SupportedMinorVersions = [0, 2, 3, 4, 5, 6, 7, 8, 9];

    internal string Magic { get; }

    internal ulong Version { get; }

    internal ulong ManifestLength { get; }

    internal uint MetadataSignatureLength { get; }

    internal ulong BaseOffset => HeaderSize + ManifestLength + MetadataSignatureLength;

    private PayloadHeader(string magic, ulong version, ulong manifestLength, uint metadataSignatureLength)
    {
        Magic = magic;
        Version = version;
        ManifestLength = manifestLength;
        MetadataSignatureLength = metadataSignatureLength;
    }

    /// <summary>
    /// Reads a payload header from the specified stream.
    /// <para>从指定的流中读取载荷头部。</para>
    /// </summary>
    internal static PayloadHeader ReadFrom(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> magicBuffer = stackalloc byte[PayloadConstants.PayloadMagic.Length];
        stream.ReadExactly(magicBuffer);

        var magic = Encoding.ASCII.GetString(magicBuffer);
        if (magic != PayloadConstants.PayloadMagic)
        {
            throw new InvalidPayloadMagicException(magic);
        }

        var version = BigEndianReader.ReadUInt64BigEndian(stream);
        if (version != BrilloMajorPayloadVersion)
        {
            throw new UnsupportedPayloadVersionException(version);
        }

        var manifestLength = BigEndianReader.ReadUInt64BigEndian(stream);
        if (manifestLength == 0 || manifestLength > MaximumManifestLength)
        {
            throw new InvalidManifestException($"Manifest length must be between 1 and {MaximumManifestLength} bytes.");
        }

        var metadataSignatureLength = BigEndianReader.ReadUInt32BigEndian(stream);
        return metadataSignatureLength == 0 || metadataSignatureLength > MaximumMetadataSignatureLength
            ? throw new InvalidManifestException($"Metadata signature length must be between 1 and {MaximumMetadataSignatureLength} bytes.")
            : new PayloadHeader(magic, version, manifestLength, metadataSignatureLength);
    }

    /// <summary>
    /// Asynchronously reads a payload header from the specified stream.
    /// <para>从指定的流中异步读取载荷头部。</para>
    /// </summary>
    internal static async Task<PayloadHeader> ReadFromAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var magicBuffer = new byte[PayloadConstants.PayloadMagic.Length];
        await stream.ReadExactlyAsync(magicBuffer, cancellationToken).ConfigureAwait(false);

        var magic = Encoding.ASCII.GetString(magicBuffer);
        if (magic != PayloadConstants.PayloadMagic)
        {
            throw new InvalidPayloadMagicException(magic);
        }

        var version = await BigEndianReader.ReadUInt64BigEndianAsync(stream).ConfigureAwait(false);
        if (version != BrilloMajorPayloadVersion)
        {
            throw new UnsupportedPayloadVersionException(version);
        }

        var manifestLength = await BigEndianReader.ReadUInt64BigEndianAsync(stream).ConfigureAwait(false);
        if (manifestLength == 0 || manifestLength > MaximumManifestLength)
        {
            throw new InvalidManifestException($"Manifest length must be between 1 and {MaximumManifestLength} bytes.");
        }

        var metadataSignatureLength = await BigEndianReader.ReadUInt32BigEndianAsync(stream).ConfigureAwait(false);
        return metadataSignatureLength == 0 || metadataSignatureLength > MaximumMetadataSignatureLength
            ? throw new InvalidManifestException($"Metadata signature length must be between 1 and {MaximumMetadataSignatureLength} bytes.")
            : new PayloadHeader(magic, version, manifestLength, metadataSignatureLength);
    }

    /// <summary>
    /// Parses a payload header from a byte array.
    /// <para>从字节数组解析载荷头部。</para>
    /// </summary>
    internal static PayloadHeader FromBytes(byte[] headerBytes)
    {
        ArgumentNullException.ThrowIfNull(headerBytes);

        if (headerBytes.Length < HeaderSize)
        {
            throw new ArgumentException($"Header bytes must be at least {HeaderSize} bytes long, but was {headerBytes.Length} bytes.", nameof(headerBytes));
        }

        var magic = Encoding.ASCII.GetString(headerBytes, 0, PayloadConstants.PayloadMagic.Length);
        if (magic != PayloadConstants.PayloadMagic)
        {
            throw new InvalidPayloadMagicException(magic);
        }

        var version = BigEndianReader.ReadUInt64BigEndian(headerBytes, 4);
        if (version != BrilloMajorPayloadVersion)
        {
            throw new UnsupportedPayloadVersionException(version);
        }

        var manifestLength = BigEndianReader.ReadUInt64BigEndian(headerBytes, 12);
        if (manifestLength == 0 || manifestLength > MaximumManifestLength)
        {
            throw new InvalidManifestException($"Manifest length must be between 1 and {MaximumManifestLength} bytes.");
        }

        var metadataSignatureLength = BigEndianReader.ReadUInt32BigEndian(headerBytes, 20);
        return metadataSignatureLength == 0 || metadataSignatureLength > MaximumMetadataSignatureLength
            ? throw new InvalidManifestException($"Metadata signature length must be between 1 and {MaximumMetadataSignatureLength} bytes.")
            : new PayloadHeader(magic, version, manifestLength, metadataSignatureLength);
    }

    /// <summary>
    /// Validates that the specified minor version is supported by this library.
    /// <para>验证指定的次版本号是否受本库支持。</para>
    /// </summary>
    internal void ValidateMinorVersion(uint minorVersion)
    {
        if (SupportedMinorVersions.Contains(minorVersion))
        {
            return;
        }

        throw new DeltaPayloadNotSupportedException(minorVersion);
    }
}

internal static class PayloadHeaderBuilder
{
    /// <summary>
    /// Builds the 24-byte CrAU header for a payload.bin file.
    /// <para>构建 payload.bin 文件的 24 字节 CrAU 头部。</para>
    /// </summary>
    internal static byte[] BuildHeader(ulong manifestLength, uint metadataSignatureLength)
    {
        using var ms = new MemoryStream();
        var magic = Encoding.ASCII.GetBytes(PayloadConstants.PayloadMagic);
        ms.Write(magic, 0, magic.Length);

        Span<byte> versionBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(versionBytes, PayloadHeader.BrilloMajorPayloadVersion);
        ms.Write(versionBytes);

        Span<byte> manifestLenBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(manifestLenBytes, manifestLength);
        ms.Write(manifestLenBytes);

        Span<byte> sigLenBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sigLenBytes, metadataSignatureLength);
        ms.Write(sigLenBytes);

        return ms.ToArray();
    }
}
