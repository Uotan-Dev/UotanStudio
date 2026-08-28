using System.Text;
using UotanStudio.Firmware.Payload.Exceptions;

namespace UotanStudio.Firmware.Payload.Core;

/// <summary>
/// Represents the 24-byte header of an Android OTA payload file.
/// <para>表示 Android OTA 载荷文件的 24 字节头部。</para>
/// </summary>
internal class PayloadHeader
{
    /// <summary>
    /// The major payload version supported by this library (version 2).
    /// <para>此库支持的主载荷版本（版本 2）。</para>
    /// </summary>
    internal const ulong BrilloMajorPayloadVersion = 2UL;

    /// <summary>
    /// The fixed size in bytes of the payload header.
    /// <para>载荷头部的固定字节大小。</para>
    /// </summary>
    internal const int HeaderSize = 24;
    private const ulong MaximumManifestLength = 512UL * 1024 * 1024;
    private const uint MaximumMetadataSignatureLength = 64U * 1024 * 1024;

    // update_engine 官方支持的 minor 版本含 7（Android 10+ 部分机型使用），
    // manifest 的 protobuf 解析不受 minor 版本影响，仅作白名单校验。
    // Official update_engine supports minor version 7 (used by some Android 10+ devices);
    // manifest protobuf parsing is version-agnostic, so this is only a whitelist check.
    private static readonly HashSet<uint> SupportedMinorVersions = [0, 2, 3, 4, 5, 6, 7, 8, 9];

    /// <summary>
    /// Gets the magic value read from the payload header, which identifies the file format.
    /// <para>获取从载荷头读取的魔数值，该值用于标识文件格式。</para>
    /// </summary>
    internal string Magic { get; }

    /// <summary>
    /// Gets the payload format version number.
    /// <para>获取载荷格式版本号。</para>
    /// </summary>
    internal ulong Version { get; }

    /// <summary>
    /// Gets the size in bytes of the protobuf manifest that follows the header.
    /// <para>获取头部之后 Protobuf 清单的字节大小。</para>
    /// </summary>
    internal ulong ManifestLength { get; }

    /// <summary>
    /// Gets the size in bytes of the metadata signature that follows the manifest.
    /// <para>获取清单之后元数据签名的字节大小。</para>
    /// </summary>
    internal uint MetadataSignatureLength { get; }

    /// <summary>
    /// Gets the computed base offset (in bytes) where the actual payload data begins.
    /// Calculated as HeaderSize + ManifestLength + MetadataSignatureLength.
    /// <para>获取实际载荷数据开始的计算偏移量（字节）。
    /// 计算公式为 HeaderSize + ManifestLength + MetadataSignatureLength。</para>
    /// </summary>
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
    /// <param name="stream">The stream to read the header from. The stream must be positioned at the beginning of the payload.
    /// <para>要从中读取头部的流。流必须定位在载荷的起始位置。</para>
    /// </param>
    /// <returns>A new <see cref="PayloadHeader"/> instance populated with the values read from the stream.
    /// <para>一个从流中读取的值填充的新 <see cref="PayloadHeader"/> 实例。</para>
    /// </returns>
    /// <exception cref="InvalidPayloadMagicException">Thrown when the magic bytes do not match the expected "CrAU" value.
    /// <para>当魔数字节与预期的"CrAU"值不匹配时抛出。</para>
    /// </exception>
    /// <exception cref="UnsupportedPayloadVersionException">Thrown when the payload version is not equal to <see cref="BrilloMajorPayloadVersion"/>.
    /// <para>当载荷版本不等于 <see cref="BrilloMajorPayloadVersion"/> 时抛出。</para>
    /// </exception>
    /// <exception cref="InvalidManifestException">Thrown when the manifest length or metadata signature length is zero.
    /// <para>当清单长度或元数据签名长度为零时抛出。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">Thrown when the stream does not contain enough bytes to read the header.
    /// <para>当流中没有足够的字节来读取头部时抛出。</para>
    /// </exception>
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
    /// <param name="stream">The stream to read the header from. The stream must be positioned at the beginning of the payload.
    /// <para>要从中读取头部的流。流必须定位在载荷的起始位置。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task that represents the asynchronous read operation. The task result contains a new <see cref="PayloadHeader"/> instance.
    /// <para>表示异步读取操作的任务。任务结果包含一个新的 <see cref="PayloadHeader"/> 实例。</para>
    /// </returns>
    /// <exception cref="InvalidPayloadMagicException">Thrown when the magic bytes do not match the expected "CrAU" value.
    /// <para>当魔数字节与预期的"CrAU"值不匹配时抛出。</para>
    /// </exception>
    /// <exception cref="UnsupportedPayloadVersionException">Thrown when the payload version is not equal to <see cref="BrilloMajorPayloadVersion"/>.
    /// <para>当载荷版本不等于 <see cref="BrilloMajorPayloadVersion"/> 时抛出。</para>
    /// </exception>
    /// <exception cref="InvalidManifestException">Thrown when the manifest length or metadata signature length is zero.
    /// <para>当清单长度或元数据签名长度为零时抛出。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">Thrown when the stream does not contain enough bytes to read the header.
    /// <para>当流中没有足够的字节来读取头部时抛出。</para>
    /// </exception>
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
    /// <param name="headerBytes">The byte array containing the header data. Must be at least <see cref="HeaderSize"/> (24) bytes long.
    /// <para>包含头部数据的字节数组。长度必须至少为 <see cref="HeaderSize"/>（24）字节。</para>
    /// </param>
    /// <returns>A new <see cref="PayloadHeader"/> instance populated with the values parsed from the byte array.
    /// <para>一个从字节数组解析的值填充的新 <see cref="PayloadHeader"/> 实例。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="headerBytes"/> is null.
    /// <para>当 <paramref name="headerBytes"/> 为 null 时抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="headerBytes"/> is shorter than <see cref="HeaderSize"/> bytes.
    /// <para>当 <paramref name="headerBytes"/> 短于 <see cref="HeaderSize"/> 字节时抛出。</para>
    /// </exception>
    /// <exception cref="InvalidPayloadMagicException">Thrown when the magic bytes do not match the expected "CrAU" value.
    /// <para>当魔数字节与预期的"CrAU"值不匹配时抛出。</para>
    /// </exception>
    /// <exception cref="UnsupportedPayloadVersionException">Thrown when the payload version is not equal to <see cref="BrilloMajorPayloadVersion"/>.
    /// <para>当载荷版本不等于 <see cref="BrilloMajorPayloadVersion"/> 时抛出。</para>
    /// </exception>
    /// <exception cref="InvalidManifestException">Thrown when the manifest length or metadata signature length is zero.
    /// <para>当清单长度或元数据签名长度为零时抛出。</para>
    /// </exception>
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
    /// <param name="minorVersion">The minor version number to validate.
    /// <para>要验证的次版本号。</para>
    /// </param>
    /// <exception cref="DeltaPayloadNotSupportedException">Thrown when the minor version is not in the set of supported values {0, 2, 3, 4, 5, 6, 8, 9}.
    /// <para>当次版本号不在支持的值集合 {0, 2, 3, 4, 5, 6, 8, 9} 中时抛出。</para>
    /// </exception>
    internal void ValidateMinorVersion(uint minorVersion)
    {
        if (SupportedMinorVersions.Contains(minorVersion))
        {
            return;
        }

        throw new DeltaPayloadNotSupportedException(minorVersion);
    }
}

/// <summary>
/// Provides utility methods for building payload header bytes.
/// <para>提供构建载荷头字节的工具方法。</para>
/// </summary>
internal static class PayloadHeaderBuilder
{
    /// <summary>
    /// Builds the 24-byte CrAU header for a payload.bin file.
    /// <para>构建 payload.bin 文件的 24 字节 CrAU 头部。</para>
    /// </summary>
    /// <param name="manifestLength">The length of the serialized manifest in bytes.
    /// <para>序列化 manifest 的字节长度。</para>
    /// </param>
    /// <param name="metadataSignatureLength">The length of the metadata signature section in bytes.
    /// <para>元数据签名段的字节长度。</para>
    /// </param>
    /// <returns>A 24-byte array containing the CrAU header.
    /// <para>包含 CrAU 头部的 24 字节数组。</para>
    /// </returns>
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