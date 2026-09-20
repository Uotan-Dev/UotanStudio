using System.Buffers.Binary;
using System.Security.Cryptography;
using BsDiff;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Lz4Diff;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class PayloadLz4DiffTests
{
    private sealed record Block(ulong Offset, ulong Length, ulong Stored, byte[]? Hash = null, byte[]? Postfix = null);

    [Fact]
    public void DestinationPclusters_MatchNativeLiblz4WithPartialBlocksAndZeroPadding()
    {
        var oldData = Enumerable.Repeat((byte)1, 65536).ToArray();
        var newData = new byte[65536];
        var native = Convert.FromHexString("1f000100fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe5000000000001f000100fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffe500000000000000000000000000000000000001f000100ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffb7500000000000");
        Block[] target = [
            new(0, 22974, 100, Convert.FromHexString("86b2f7855925f3aee52a061b4bf5351a3cd49cf89b0f5bfd2d5d70ad1615d77e")),
            new(22974, 22974, 100, Convert.FromHexString("86b2f7855925f3aee52a061b4bf5351a3cd49cf89b0f5bfd2d5d70ad1615d77e")),
            new(45948, 19588, 100, Convert.FromHexString("3e8d84a921afa6656411ab5b8adfc74e8e07f9eb4022eb57f1dc61a8c0485542"))
        ];
        var patch = Build(Patch(oldData, newData), [new(0, 65536, 65536)], target, 2, true);
        Assert.Equal(native, Lz4DiffApplier.Apply(oldData, patch, native.Length));

        // Also checks the reverse source-block decoding against native compressed bytes.
        var reverse = Build(Patch(newData, oldData), target, [new(0, 65536, 65536)], 2, true);
        Assert.Equal(oldData, Lz4DiffApplier.Apply(native, reverse, oldData.Length));
    }

    [Fact]
    public void StandardContainer_AppliesRecompressionHashAndPostfix()
    {
        byte[] source = [1, 2, 3, 4], middle = [5, 6, 7, 8], target = [9, 10, 11, 12];
        var postfix = Patch(middle, target);
        var patch = Build(Patch(source, middle), [new(0, 4, 4)],
            [new(0, 4, 4, SHA256.HashData(middle), postfix)], 1, false);
        Assert.Equal(target, Lz4DiffApplier.Apply(source, patch, 4));
        var wrongHash = Build(Patch(source, middle), [new(0, 4, 4)],
            [new(0, 4, 4, SHA256.HashData(target), postfix)], 1, false);
        Assert.Throws<InvalidDataException>(() => Lz4DiffApplier.Apply(source, wrongHash, 4));
    }

    internal static byte[] BuildStoredPatch(int length, byte[] inner)
        => Build(inner, [new(0, (ulong)length, (ulong)length)], [new(0, (ulong)length, (ulong)length)], 1, false);

    private static byte[] Build(byte[] inner, Block[] source, Block[] target, int algorithm, bool zeroPadding)
    {
        // Field tags follow update_engine/lz4diff/lz4diff.proto at fc83053aa2a71885ba8615da9946033adf3462ce.
        var header = Message(writer =>
        {
            Bytes(writer, 1, Info(source, algorithm, zeroPadding));
            Bytes(writer, 2, Info(target, algorithm, zeroPadding));
        });
        var patch = new byte[16 + header.Length + inner.Length];
        "LZ4DIFF"u8.CopyTo(patch);
        BinaryPrimitives.WriteUInt32BigEndian(patch.AsSpan(7), 1);
        BinaryPrimitives.WriteUInt32BigEndian(patch.AsSpan(11), (uint)header.Length);
        header.CopyTo(patch, 16); inner.CopyTo(patch, 16 + header.Length);
        return patch;
    }
    private static byte[] Info(Block[] blocks, int algorithm, bool padding) => Message(writer =>
    {
        Bytes(writer, 1, Message(child => { Number(child, 1, (ulong)algorithm); Number(child, 2, 9); }));
        foreach (var block in blocks)
            Bytes(writer, 2, Message(child =>
            {
                Number(child, 1, block.Offset); Number(child, 2, block.Length); Number(child, 3, block.Stored);
                if (block.Hash is not null) Bytes(child, 4, block.Hash);
                if (block.Postfix is not null) Bytes(child, 5, block.Postfix);
            }));
        Number(writer, 3, padding ? 1UL : 0);
    });
    private static void Number(CodedOutputStream writer, int field, ulong value) { writer.WriteTag(field, WireFormat.WireType.Varint); writer.WriteUInt64(value); }
    private static void Bytes(CodedOutputStream writer, int field, byte[] value) { writer.WriteTag(field, WireFormat.WireType.LengthDelimited); writer.WriteBytes(ByteString.CopyFrom(value)); }
    private static byte[] Message(Action<CodedOutputStream> write) { using var result = new MemoryStream(); using (var writer = new CodedOutputStream(result, true)) write(writer); return result.ToArray(); }
    private static byte[] Patch(byte[] source, byte[] target) { using var patch = new MemoryStream(); BinaryPatch.Create(source, target, patch); return patch.ToArray(); }
}