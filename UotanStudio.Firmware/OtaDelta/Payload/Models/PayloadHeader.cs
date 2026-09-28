using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.OtaDelta.Payload;

namespace UotanStudio.Firmware.OtaDelta.Payload.Models;

internal class PayloadHeader
{
    internal const ulong BrilloMajorPayloadVersion = 2UL;

    internal const int HeaderSize = 24;
    private const ulong MaximumManifestLength = 512UL * 1024 * 1024;
    private const uint MaximumMetadataSignatureLength = 64U * 1024 * 1024;


    /// <summary>
    /// Manifest minor versions accepted by this library. Minor version 1 is deprecated upstream and never
    /// accepted; 10 is the upstream minimum that permits REPLACE_ZSTD operations.
    /// <para>本库接受的清单 minor 版本集合。minor 1 在上游已废弃、永不接受；10 是上游允许
    /// REPLACE_ZSTD 操作的最低版本。</para>
    /// </summary>
    internal static readonly HashSet<uint> SupportedMinorVersions = [0, 2, 3, 4, 5, 6, 7, 8, 9, 10];

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

        var version = BinaryReaderHelpers.ReadUInt64BE(stream);
        if (version != BrilloMajorPayloadVersion)
        {
            throw new UnsupportedPayloadVersionException(version);
        }

        var manifestLength = BinaryReaderHelpers.ReadUInt64BE(stream);
        if (manifestLength == 0 || manifestLength > MaximumManifestLength)
        {
            throw new InvalidManifestException($"Manifest length must be between 1 and {MaximumManifestLength} bytes.");
        }

        var metadataSignatureLength = BinaryReaderHelpers.ReadUInt32BE(stream);
        return metadataSignatureLength > MaximumMetadataSignatureLength
            ? throw new InvalidManifestException($"Metadata signature length must be between 0 and {MaximumMetadataSignatureLength} bytes.")
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

        var version = await BinaryReaderHelpers.ReadUInt64BEAsync(stream, cancellationToken).ConfigureAwait(false);
        if (version != BrilloMajorPayloadVersion)
        {
            throw new UnsupportedPayloadVersionException(version);
        }

        var manifestLength = await BinaryReaderHelpers.ReadUInt64BEAsync(stream, cancellationToken).ConfigureAwait(false);
        if (manifestLength == 0 || manifestLength > MaximumManifestLength)
        {
            throw new InvalidManifestException($"Manifest length must be between 1 and {MaximumManifestLength} bytes.");
        }

        var metadataSignatureLength = await BinaryReaderHelpers.ReadUInt32BEAsync(stream, cancellationToken).ConfigureAwait(false);
        return metadataSignatureLength > MaximumMetadataSignatureLength
            ? throw new InvalidManifestException($"Metadata signature length must be between 0 and {MaximumMetadataSignatureLength} bytes.")
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

        var version = BinaryReaderHelpers.ReadUInt64BE(headerBytes, 4);
        if (version != BrilloMajorPayloadVersion)
        {
            throw new UnsupportedPayloadVersionException(version);
        }

        var manifestLength = BinaryReaderHelpers.ReadUInt64BE(headerBytes, 12);
        if (manifestLength == 0 || manifestLength > MaximumManifestLength)
        {
            throw new InvalidManifestException($"Manifest length must be between 1 and {MaximumManifestLength} bytes.");
        }

        var metadataSignatureLength = BinaryReaderHelpers.ReadUInt32BE(headerBytes, 20);
        return metadataSignatureLength > MaximumMetadataSignatureLength
            ? throw new InvalidManifestException($"Metadata signature length must be between 0 and {MaximumMetadataSignatureLength} bytes.")
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