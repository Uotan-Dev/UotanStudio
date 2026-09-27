using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AvbDescriptorBoundaryTests
{
    [Theory]
    [InlineData(0, 16, 0)]
    [InlineData(1, 168, 88)]
    [InlineData(2, 120, 40)]
    [InlineData(3, 8, 4)]
    [InlineData(4, 80, 4)]
    public void ReadDescriptorsStrict_FieldSpillsIntoNextDescriptor_RejectsEntireTable(ulong tag, int size, int lengthOffset)
    {
        var body = new byte[size];
        if (tag == 0)
        {
            BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(lengthOffset, 8), 12);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(lengthOffset, 4), 12);
        }
        var bytes = Container(Descriptor(tag, body), Descriptor(99, new byte[64]));

        Assert.Throws<InvalidDataException>(() => AvbSignatureVerifier.ReadDescriptorsStrict(bytes));
        Assert.Empty(AvbSignatureVerifier.ExtractDescriptors(bytes));
        var verification = AvbSignatureVerifier.Verify(bytes);
        Assert.False(verification.SignatureIsValid);
        Assert.Contains("descriptor", verification.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(96)]
    [InlineData(104)]
    public void ReadDescriptorsStrict_UnsignedHeaderOverflow_RejectsWithoutOverflowException(int offset)
    {
        var bytes = Container(Descriptor(99, []));
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset, 8), ulong.MaxValue);

        Assert.Throws<InvalidDataException>(() => AvbSignatureVerifier.ReadDescriptorsStrict(bytes));
        Assert.Empty(AvbSignatureVerifier.ExtractDescriptors(bytes));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(7UL)]
    [InlineData(ulong.MaxValue)]
    public void ReadDescriptorsStrict_TruncatedOrMisalignedDescriptor_Rejects(ulong length)
    {
        var descriptor = Descriptor(2, new byte[120]);
        BinaryPrimitives.WriteUInt64BigEndian(descriptor.AsSpan(8, 8), length);
        var bytes = Container(descriptor);

        Assert.Throws<InvalidDataException>(() => AvbSignatureVerifier.ReadDescriptorsStrict(bytes));
    }

    [Theory]
    [InlineData(0xff)]
    [InlineData(0)]
    public void ReadDescriptorsStrict_InvalidPartitionUtf8OrNul_Rejects(int character)
    {
        var body = new byte[120];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(40, 4), 1);
        body[116] = (byte)character;

        Assert.Throws<InvalidDataException>(() => AvbSignatureVerifier.ReadDescriptorsStrict(Container(Descriptor(2, body))));
    }

    [Fact]
    public void ReadDescriptorsStrict_PropertyWithoutTerminators_Rejects()
    {
        var body = new byte[24];
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(8, 8), 1);
        body[16] = (byte)'a';
        body[17] = 1;
        body[18] = (byte)'b';

        Assert.Throws<InvalidDataException>(() => AvbSignatureVerifier.ReadDescriptorsStrict(Container(Descriptor(0, body))));
    }

    [Fact]
    public void ReadDescriptorsStrict_ChainFlags_PreservesDoNotUseAb()
    {
        var body = new byte[80];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12, 4), 1);
        body[76] = (byte)'x';
        body[77] = 42;

        var descriptor = Assert.Single(AvbSignatureVerifier.ReadDescriptorsStrict(Container(Descriptor(4, body))));

        Assert.Equal(1U, descriptor.Flags);
        Assert.Equal("x", descriptor.PartitionName);
        Assert.Equal(new byte[] { 42 }, descriptor.PublicKey);
    }

    [Fact]
    public void ReadDescriptorsStrict_UnknownTag_PreservesWithoutInterpreting()
    {
        var descriptor = Assert.Single(AvbSignatureVerifier.ReadDescriptorsStrict(Container(Descriptor(99, new byte[8]))));

        Assert.Equal(99UL, descriptor.Tag);
        Assert.Equal("unknown", descriptor.TypeName);
    }

    [Fact]
    public void ReadDescriptorsStrict_TooManyDescriptors_RejectsWithoutPartialResult()
    {
        var descriptors = Enumerable.Range(0, 4097).Select(_ => Descriptor(99, [])).ToArray();
        var bytes = Container(descriptors);

        Assert.Throws<InvalidDataException>(() => AvbSignatureVerifier.ReadDescriptorsStrict(bytes));
        Assert.Empty(AvbSignatureVerifier.ExtractDescriptors(bytes));
    }

    [Fact]
    public void ReadDescriptorsStrict_TrailingPartialHeader_RejectsWithoutPartialResult()
    {
        var bytes = Container(Descriptor(99, []), [0]);

        Assert.Throws<InvalidDataException>(() => AvbSignatureVerifier.ReadDescriptorsStrict(bytes));
        Assert.Empty(AvbSignatureVerifier.ExtractDescriptors(bytes));
    }

    private static byte[] Container(params byte[][] descriptors)
    {
        var data = descriptors.SelectMany(value => value).ToArray();
        var result = new byte[256 + data.Length];
        "AVB0"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(20, 8), (ulong)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(104, 8), (ulong)data.Length);
        data.CopyTo(result, 256);
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
}