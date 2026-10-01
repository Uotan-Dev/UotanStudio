using System.Buffers.Binary;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;

namespace UotanStudio.Tests.Firmware;

internal static class ArtVdexFixture
{
    internal static (byte[] Vdex, byte[] Dex) Fixture(int count, int version = 6, int padding = 0)
    {
        var dex = SmaliAssembler.Assemble($$"""
            .class public Lexample/Test;
            .super Ljava/lang/Object;
            .field public field:I
            .field public wide:J
            .field public object:Ljava/lang/Object;
            .field public boolean:Z
            .field public byte:B
            .field public char:C
            .field public short:S
            .method public static run(Lexample/Test;)V
                .registers 8
                {{string.Concat(Enumerable.Repeat("nop\n", padding))}}
                iget v0, p0, Lexample/Test;->field:I
                iput v0, p0, Lexample/Test;->field:I
                iget-wide v0, p0, Lexample/Test;->wide:J
                iput-wide v0, p0, Lexample/Test;->wide:J
                iget-object v0, p0, Lexample/Test;->object:Ljava/lang/Object;
                iput-object v0, p0, Lexample/Test;->object:Ljava/lang/Object;
                invoke-virtual {p0}, Ljava/lang/Object;->toString()Ljava/lang/String;
                invoke-virtual/range {p0 .. p0}, Ljava/lang/Object;->toString()Ljava/lang/String;
                iget-boolean v0, p0, Lexample/Test;->boolean:Z
                iput-boolean v0, p0, Lexample/Test;->boolean:Z
                iget-byte v0, p0, Lexample/Test;->byte:B
                iput-byte v0, p0, Lexample/Test;->byte:B
                iget-char v0, p0, Lexample/Test;->char:C
                iput-char v0, p0, Lexample/Test;->char:C
                iget-short v0, p0, Lexample/Test;->short:S
                iput-short v0, p0, Lexample/Test;->short:S
                check-cast p0, Lexample/Test;
                nop
                return-void
            .end method
            .method public static zHelper()V
                .registers 0
                return-void
            .end method
            .method public native zNative()V
            .end method
            .method public zVirtual()V
                .registers 1
                return-void
            .end method
            .method public static zPacked(I)V
                .registers 1
                packed-switch p0, :table
                return-void
            :target
                return-void
            :table
                .packed-switch 0
                    :target
                .end packed-switch
            .end method
            .method public static zSparse(I)V
                .registers 1
                sparse-switch p0, :table
                return-void
            :target
                return-void
            :table
                .sparse-switch
                    42 -> :target
                .end sparse-switch
            .end method
            .method public static zArray([B)V
                .registers 1
                fill-array-data p0, :table
                return-void
            :table
                .array-data 1
                    0x73
                    0xe3
                    0xea
                .end array-data
            .end method
            """).ToArray();
        var quickened = (byte[])dex.Clone();
        var classData = (int)U32(dex, (int)U32(dex, 100) + 24);
        var fields = ReadLeb(dex, ref classData) + ReadLeb(dex, ref classData);
        var methods = ReadLeb(dex, ref classData) + ReadLeb(dex, ref classData);
        for (var i = 0; i < fields; i++)
        {
            _ = ReadLeb(dex, ref classData);
            _ = ReadLeb(dex, ref classData);
        }
        var codeItems = new List<int>();
        for (var i = 0; i < methods; i++)
        {
            _ = ReadLeb(dex, ref classData);
            _ = ReadLeb(dex, ref classData);
            var code = ReadLeb(dex, ref classData);
            if (code != 0)
            {
                codeItems.Add(code);
            }
        }
        var blobs = new List<byte[]>();
        byte[] normal = [0x52, 0x53, 0x54, 0x59, 0x5a, 0x5b, 0x6e, 0x74, 0x5c, 0x5d, 0x5e, 0x5f, 0x55, 0x56, 0x57, 0x58];
        foreach (var code in codeItems)
        {
            var quick = new List<byte>();
            var quick010 = new List<byte>();
            var units = U32(dex, code + 12);
            for (var pc = 0; pc < units;)
            {
                var offset = code + 16 + pc * 2;
                var op = dex[offset];
                var size = OpCodes.GetSize(op);
                var word = BinaryPrimitives.ReadUInt16LittleEndian(dex.AsSpan(offset));
                if (word is 0x100 or 0x200 or 0x300)
                {
                    var n = BinaryPrimitives.ReadUInt16LittleEndian(dex.AsSpan(offset + 2));
                    size = word switch
                    {
                        0x100 => 4 + 2 * n,
                        0x200 => 2 + 4 * n,
                        _ => 4 + (n * (int)U32(dex, offset + 4) + 1) / 2
                    };
                }
                var quickIndex = Array.IndexOf(normal, op);
                if (quickIndex >= 0)
                {
                    quickened[offset] = (byte)(0xe3 + quickIndex);
                    AddLeb(quick, pc);
                    AddLeb(quick, BinaryPrimitives.ReadUInt16LittleEndian(dex.AsSpan(offset + 2)));
                    quick010.AddRange(dex.AsSpan(offset + 2, 2).ToArray());
                    quickened[offset + 2] = 123;
                    quickened[offset + 3] = 0;
                }
                else if (op == 0x1f)
                {
                    AddLeb(quick, pc);
                    AddLeb(quick, dex[offset + 1]);
                    AddLeb(quick, pc);
                    AddLeb(quick, BinaryPrimitives.ReadUInt16LittleEndian(dex.AsSpan(offset + 2)));
                    quick010.Add(dex[offset + 1]);
                    quick010.Add(0);
                    quick010.AddRange(dex.AsSpan(offset + 2, 2).ToArray());
                    quickened.AsSpan(offset, 4).Clear();
                }
                else if (op == 0x0e)
                {
                    quickened[offset] = 0x73;
                }
                else if (op == 0)
                {
                    quick010.AddRange([255, 255]);
                }
                pc += size;
            }
            blobs.Add((version == 6 ? quick : quick010).ToArray());
        }
        var header = 24 + 4 * count;
        var blobBytesPerDex = blobs.Sum(b => 4 + b.Length);
        if (version == 10)
        {
            var quickStart = header + dex.Length * count;
            var tables = blobBytesPerDex * count;
            var quickSize = tables + count * (8 * codeItems.Count + 4);
            var output = new byte[quickStart + quickSize];
            "vdex010\0"u8.CopyTo(output);
            Put32(output, 8, (uint)count);
            Put32(output, 12, (uint)(dex.Length * count));
            Put32(output, 20, (uint)quickSize);
            for (var i = 0; i < count; i++)
            {
                Put32(output, 24 + i * 4, U32(dex, 8));
                quickened.CopyTo(output, header + i * dex.Length);
                var blob = blobBytesPerDex * i;
                var table = tables + i * 8 * codeItems.Count;
                for (var j = 0; j < codeItems.Count; j++)
                {
                    Put32(output, quickStart + blob, (uint)blobs[j].Length);
                    blobs[j].CopyTo(output, quickStart + blob + 4);
                    Put32(output, quickStart + table + j * 8, (uint)codeItems[j]);
                    Put32(output, quickStart + table + j * 8 + 4, (uint)blob);
                    blob += 4 + blobs[j].Length;
                }
                Put32(output, quickStart + tables + count * codeItems.Count * 8 + i * 4, (uint)table);
            }
            return (output, dex);
        }
        var bytes = new byte[header + dex.Length * count + 3 + blobBytesPerDex * count];
        "vdex006\0"u8.CopyTo(bytes);
        Put32(bytes, 8, (uint)count);
        Put32(bytes, 12, (uint)(dex.Length * count));
        Put32(bytes, 16, 3);
        Put32(bytes, 20, (uint)(blobBytesPerDex * count));
        for (var i = 0; i < count; i++)
        {
            Put32(bytes, 24 + 4 * i, U32(dex, 8));
            quickened.CopyTo(bytes, header + dex.Length * i);
            var at = header + dex.Length * count + 3 + blobBytesPerDex * i;
            foreach (var blob in blobs)
            {
                Put32(bytes, at, (uint)blob.Length);
                blob.CopyTo(bytes, at + 4);
                at += 4 + blob.Length;
            }
        }
        return (bytes, dex);
    }

    private static int ReadLeb(byte[] bytes, ref int offset)
    {
        var result = 0;
        var shift = 0;
        byte value;
        do
        {
            value = bytes[offset++];
            result |= (value & 127) << shift;
            shift += 7;
        } while ((value & 128) != 0);
        return result;
    }

    private static void AddLeb(List<byte> bytes, int value)
    {
        do
        {
            var b = (byte)(value & 127);
            value >>= 7;
            bytes.Add(value == 0 ? b : (byte)(b | 128));
        } while (value != 0);
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
}