using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbDescriptorReaderTests
{
    [Fact]
    public void ExtractHashDescriptors_FullHeader_MatchesStrictParserOnPinnedDescriptor()
    {
        var salt = new byte[] { 0x10, 0x20, 0x30 };
        var digest = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var bytes = Container(
            Descriptor(0, PropertyBody("avb.test", [1, 2])),
            Descriptor(2, HashBody("system", salt, digest, 0x4000, 3)));

        var descriptor = Assert.Single(AvbDescriptorReader.ExtractHashDescriptors(bytes));

        Assert.Equal(2UL, descriptor.Tag);
        Assert.Equal(0x4000UL, descriptor.ImageSize);
        Assert.Equal("sha256", descriptor.HashAlgorithm);
        Assert.Equal("system", descriptor.PartitionName);
        Assert.Equal(salt, descriptor.Salt);
        Assert.Equal(digest, descriptor.Digest);
        Assert.Equal(3U, descriptor.Flags);
        Assert.Equal(0U, descriptor.DmVerityVersion);
        Assert.Equal(0UL, descriptor.HashBlockSize);

        var strict = Assert.Single(
            AvbSignatureVerifier.ReadDescriptorsStrict(bytes).Where(item => item.Tag == 2).ToArray());
        Assert.Equal(strict.ImageSize, descriptor.ImageSize);
        Assert.Equal(strict.HashAlgorithm, descriptor.HashAlgorithm);
        Assert.Equal(strict.PartitionName, descriptor.PartitionName);
        Assert.Equal(strict.Salt, descriptor.Salt);
        Assert.Equal(strict.Digest, descriptor.Digest);
        Assert.Equal(strict.Flags, descriptor.Flags);
    }

    [Fact]
    public void ExtractHashDescriptors_ShortHeader_ScansAuxiliaryBlock()
    {
        var descriptor = Descriptor(2, HashBody("vendor", [9], [8, 7, 6], 512));
        var bytes = ShortContainer(descriptor);

        var parsed = Assert.Single(AvbDescriptorReader.ExtractHashDescriptors(bytes));

        Assert.Equal("vendor", parsed.PartitionName);
        Assert.Equal(512UL, parsed.ImageSize);
        Assert.Equal(new byte[] { 9 }, parsed.Salt);
        Assert.Equal(new byte[] { 8, 7, 6 }, parsed.Digest);
    }

    [Fact]
    public void ExtractHashDescriptors_PropertyDescriptorOnly_ReturnsEmpty()
    {
        var bytes = Container(Descriptor(0, PropertyBody("avb.test", [1])));

        Assert.Empty(AvbDescriptorReader.ExtractHashDescriptors(bytes));
    }

    [Fact]
    public void ExtractHashDescriptors_DescriptorLengthOverflowsRegion_ReturnsEmptyWithoutThrowing()
    {
        var bytes = Container(Descriptor(2, HashBody("system", [], [], 1)));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(264, 8), ulong.MaxValue);

        Assert.Empty(AvbDescriptorReader.ExtractHashDescriptors(bytes));
    }

    [Fact]
    public void ExtractHashDescriptors_DescriptorBodyTooShort_IsSkipped()
    {
        var truncated = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(truncated.AsSpan(0, 8), 1);
        var bytes = Container(Descriptor(2, truncated), Descriptor(2, HashBody("kept", [], [], 1)));

        var parsed = Assert.Single(AvbDescriptorReader.ExtractHashDescriptors(bytes));

        Assert.Equal("kept", parsed.PartitionName);
    }

    [Fact]
    public void ExtractHashDescriptors_DeclaredAuxiliaryBeyondImage_ReturnsEmpty()
    {
        var bytes = Container(Descriptor(2, HashBody("system", [], [], 1)));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(20, 8), ulong.MaxValue);

        Assert.Empty(AvbDescriptorReader.ExtractHashDescriptors(bytes));
    }

    [Fact]
    public void ExtractHashDescriptors_NonVbmetaOrTooShort_ReturnsEmpty()
    {
        Assert.Empty(AvbDescriptorReader.ExtractHashDescriptors(new byte[300]));
        Assert.Empty(AvbDescriptorReader.ExtractHashDescriptors("not an AVB image"u8.ToArray()));
    }

    [Fact]
    public void ExtractHashDescriptors_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AvbDescriptorReader.ExtractHashDescriptors(null!));
    }

    private static byte[] Container(params byte[][] descriptors)
    {
        var data = descriptors.SelectMany(value => value).ToArray();
        var result = new byte[256 + data.Length];
        "AVB0"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(20, 8), (ulong)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(104, 8), (ulong)data.Length);
        data.CopyTo(result, 256);
        return result;
    }

    private static byte[] ShortContainer(params byte[][] descriptors)
    {
        var data = descriptors.SelectMany(value => value).ToArray();
        var result = new byte[80 + data.Length];
        "AVB0"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(20, 8), (ulong)data.Length);
        data.CopyTo(result, 80);
        return result;
    }

    private static byte[] Descriptor(ulong tag, byte[] body)
    {
        var result = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(0, 8), tag);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8, 8), (ulong)body.Length);
        body.CopyTo(result, 16);
        return result;
    }

    private static byte[] HashBody(string partitionName, byte[] salt, byte[] digest, ulong imageSize, uint flags = 0)
    {
        var nameBytes = Encoding.ASCII.GetBytes(partitionName);
        var used = 116 + nameBytes.Length + salt.Length + digest.Length;
        var body = new byte[(used + 7) & ~7];
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), imageSize);
        Encoding.ASCII.GetBytes("sha256").CopyTo(body, 8);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(40, 4), (uint)nameBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(44, 4), (uint)salt.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(48, 4), (uint)digest.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(52, 4), flags);
        nameBytes.CopyTo(body, 116);
        salt.CopyTo(body, 116 + nameBytes.Length);
        digest.CopyTo(body, 116 + nameBytes.Length + salt.Length);
        return body;
    }

    private static byte[] PropertyBody(string key, byte[] value)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var used = 16 + keyBytes.Length + 1 + value.Length + 1;
        var body = new byte[(used + 7) & ~7];
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), (ulong)keyBytes.Length);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(8, 8), (ulong)value.Length);
        keyBytes.CopyTo(body, 16);
        value.CopyTo(body, 16 + keyBytes.Length + 1);
        return body;
    }
}