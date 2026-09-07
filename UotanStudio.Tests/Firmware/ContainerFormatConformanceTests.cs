using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Avb;
using UotanStudio.Firmware.BootImage;
using UotanStudio.Firmware.Sparse;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks boot, sparse and AVB fields against independent format definitions. <para>依据独立格式定义检查 Boot、Sparse 和 AVB 字段。</para></summary>
public class ContainerFormatConformanceTests
{
    [Theory]
    [InlineData(BootImageVersion.V0)]
    [InlineData(BootImageVersion.V1)]
    [InlineData(BootImageVersion.V2)]
    public void BootLegacyCommandLine_PreservesUtf8Across511ByteBoundary(BootImageVersion version)
    {
        var commandLine = new string('a', 510) + "中" + new string('b', 100);
        var packer = BootPacker(version, commandLine);
        var data = Pack(packer);
        var expected = Encoding.UTF8.GetBytes(commandLine);
        Assert.Equal(expected.AsSpan(0, 511).ToArray(), data.AsSpan(64, 511).ToArray());
        Assert.Equal(0, data[64 + 511]);
        Assert.Equal(expected.AsSpan(511).ToArray(), data.AsSpan(608, expected.Length - 511).ToArray());
        Assert.Equal(0, data[608 + expected.Length - 511]);
        using var parser = new BootImageParser();
        parser.Parse(data);
        Assert.Equal(commandLine, parser.Header.CommandLine);
    }

    [Theory]
    [InlineData(BootImageVersion.V0)]
    [InlineData(BootImageVersion.V1)]
    [InlineData(BootImageVersion.V2)]
    public void BootImageId_MatchesMkbootimgComponentAndLengthSequence(BootImageVersion version)
    {
        var packer = BootPacker(version, "test");
        packer.Header.Ids = Enumerable.Repeat(0xababababu, 8).ToArray();
        var data = Pack(packer);
        using var signed = new MemoryStream();
        using var writer = new BinaryWriter(signed, Encoding.UTF8, true);
        var parts = new List<byte[]> { packer.Kernel, packer.Ramdisk, packer.Second };
        if (version >= BootImageVersion.V1) parts.Add(packer.RecoveryDtbo);
        if (version >= BootImageVersion.V2) parts.Add(packer.Dtb);
        foreach (var part in parts) { writer.Write(part); writer.Write((uint)part.Length); }
        var expected = SHA1.HashData(signed.ToArray());
        Assert.Equal(expected, data.AsSpan(576, 20).ToArray());
        Assert.Equal(new byte[12], data.AsSpan(596, 12).ToArray());
        if (version >= BootImageVersion.V1)
            Assert.Equal(version == BootImageVersion.V2 ? 1660u : 1648u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1644)));
        if (version == BootImageVersion.V2)
        {
            Assert.Equal((uint)packer.Dtb.Length, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1648)));
            Assert.Equal(0x123456789abcdef0ul, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(1652)));
        }
        packer.Kernel[0] ^= 1;
        Assert.NotEqual(data.AsSpan(576, 20).ToArray(), Pack(packer).AsSpan(576, 20).ToArray());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public void SparseRawChunk_MustMatchDeclaredTotalBlocks(uint totalBlocks)
    {
        var image = SparseImage(totalBlocks, 0, (0xcac1, 1, new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<InvalidDataException>(() => SparseReader.FromBuffer(image));
    }

    [Fact]
    public void SparseMissingChunks_CannotSynthesizeDeclaredImage()
        => Assert.Throws<InvalidDataException>(() => SparseReader.FromBuffer(SparseImage(1, 0)));

    [Fact]
    public void SparseHeaderChecksum_CoversExpandedRawFillAndDontCareData()
    {
        byte[] expanded = [1, 2, 3, 4, 0x10, 0x20, 0x30, 0x40, 0x10, 0x20, 0x30, 0x40, 0, 0, 0, 0];
        var expected = ReferenceCrc32(expanded);
        var image = SparseImage(4, expected, (0xcac1, 1, new byte[] { 1, 2, 3, 4 }),
            (0xcac2, 2, new byte[] { 0x10, 0x20, 0x30, 0x40 }), (0xcac3, 1, Array.Empty<byte>()));
        using (var parsed = SparseReader.FromBuffer(image, validateCrc: true)) Assert.Equal(4u, parsed.Header.TotalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), expected ^ 1);
        Assert.Throws<InvalidDataException>(() => SparseReader.FromBuffer(image, validateCrc: true));
        using var uncheckedImage = SparseReader.FromBuffer(image, validateCrc: false);
        Assert.Equal(4u, uncheckedImage.Header.TotalBlocks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseCrcChunk_MustNotClaimOutputBlocks(bool validateCrc)
    {
        var crc = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(crc, ReferenceCrc32([1, 2, 3, 4]));
        var image = SparseImage(2, 0, (0xcac1, 1, new byte[] { 1, 2, 3, 4 }), (0xcac4, 1, crc));
        Assert.Throws<InvalidDataException>(() => SparseReader.FromBuffer(image, validateCrc));
    }

    [Fact]
    public void SparseCrcChunk_ZeroBlocksValidatesPrecedingData()
    {
        var crc = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(crc, ReferenceCrc32([1, 2, 3, 4]));
        var image = SparseImage(1, 0, (0xcac1, 1, new byte[] { 1, 2, 3, 4 }), (0xcac4, 0, crc));
        using (var parsed = SparseReader.FromBuffer(image, validateCrc: true)) Assert.Equal(1u, parsed.Header.TotalBlocks);
        image[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => SparseReader.FromBuffer(image, validateCrc: true));
    }

    [Theory]
    [InlineData(2048, 1u, true)]
    [InlineData(4096, 2u, true)]
    [InlineData(2048, 2u, false)]
    [InlineData(4096, 1u, false)]
    public void AvbAlgorithm_MustMatchActualRsaKeyAndSignature(int keyBits, uint algorithm, bool expected)
    {
        using var key = RSA.Create(keyBits);
        var (image, publicKey, signature, signedData) = SignedVbmeta(key, algorithm);
        Assert.True(key.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal(expected, AvbSignatureVerifier.Verify(image).SignatureIsValid);
        Assert.Equal(expected, AvbSignatureVerifier.Verify(image, key).SignatureIsValid);
        Assert.Equal(expected, AvbSignatureVerifier.VerifySignature(image, signature, publicKey, algorithm).SignatureIsValid);
    }

    [Fact]
    public void AvbSignatureSize_MustEqualAlgorithmWidthEvenWhenRangeFits()
    {
        using var key = RSA.Create(2048);
        var (image, blob, signature, _) = SignedVbmeta(key, 1);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(56), 255);
        Assert.False(AvbSignatureVerifier.Verify(image).SignatureIsValid);
        Assert.False(AvbSignatureVerifier.VerifySignature(image, signature[..255], blob, 1).SignatureIsValid);
    }

    private static BootImagePacker BootPacker(BootImageVersion version, string commandLine) => new()
    {
        Header = new BootImageHeader
        {
            HeaderVersion = version,
            PageSize = 2048,
            CommandLine = commandLine,
            KernelLoadAddr = 0x123456789abcdef0ul
        },
        Kernel = [1, 3, 5],
        Ramdisk = [2, 4],
        Second = [6],
        RecoveryDtbo = [7, 8, 9, 10],
        Dtb = [11, 12, 13]
    };

    private static byte[] Pack(BootImagePacker packer)
    {
        using var output = new MemoryStream(); packer.PackToStream(output); return output.ToArray();
    }

    private static byte[] SparseImage(uint blocks, uint checksum, params (ushort Type, uint Blocks, byte[] Data)[] chunks)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0xed26ff3au); writer.Write((ushort)1); writer.Write((ushort)0);
        writer.Write((ushort)28); writer.Write((ushort)12); writer.Write(4u);
        writer.Write(blocks); writer.Write((uint)chunks.Length); writer.Write(checksum);
        foreach (var chunk in chunks)
        {
            writer.Write(chunk.Type); writer.Write((ushort)0); writer.Write(chunk.Blocks);
            writer.Write((uint)(12 + chunk.Data.Length)); writer.Write(chunk.Data);
        }
        return stream.ToArray();
    }

    private static uint ReferenceCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
        }
        return ~crc;
    }

    private static (byte[] Image, byte[] PublicKey, byte[] Signature, byte[] SignedData) SignedVbmeta(RSA key, uint algorithm)
    {
        var modulus = key.ExportParameters(false).Modulus!;
        var bits = modulus.Length * 8;
        var blob = new byte[8 + modulus.Length * 2];
        BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)bits);
        var low = BinaryPrimitives.ReadUInt32BigEndian(modulus.AsSpan(modulus.Length - 4));
        uint inverse = 1;
        for (var i = 0; i < 5; i++) inverse = unchecked(inverse * (2 - low * inverse));
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), unchecked(0 - inverse));
        modulus.CopyTo(blob, 8);
        var rr = ((BigInteger.One << (2 * bits)) % new BigInteger(modulus, true, true)).ToByteArray(true, true);
        rr.CopyTo(blob, blob.Length - rr.Length);
        var authSize = (32 + modulus.Length + 63) & ~63;
        var auxSize = (blob.Length + 63) & ~63;
        var header = new byte[256]; "AVB0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(12), (ulong)authSize);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(20), (ulong)auxSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), algorithm);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(40), 32);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(48), 32);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(56), (ulong)modulus.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(72), (ulong)blob.Length);
        var signedData = new byte[256 + auxSize]; header.CopyTo(signedData, 0); blob.CopyTo(signedData, 256);
        var signature = key.SignData(signedData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var image = new byte[256 + authSize + auxSize]; header.CopyTo(image, 0);
        SHA256.HashData(signedData).CopyTo(image, 256); signature.CopyTo(image, 288); blob.CopyTo(image, 256 + authSize);
        return (image, blob, signature, signedData);
    }
}