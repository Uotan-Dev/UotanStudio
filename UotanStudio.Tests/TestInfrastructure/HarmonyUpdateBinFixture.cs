using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.VendorFirmware.HarmonyOS;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Builds synthetic HarmonyOS <c>update.bin</c> packages for tests: the L1/L2 component table, reserved field,
/// metadata TLV and concatenated payload are assembled in memory so format detection and extraction can be
/// exercised without a real firmware package.
/// <para>为测试构建合成 HarmonyOS <c>update.bin</c> 包：在内存中组装 L1/L2 组件表、保留字段、元数据 TLV
/// 与拼接载荷，使格式探测与解包无需真实固件包即可验证。</para>
/// </summary>
internal static class HarmonyUpdateBinFixture
{
    internal static byte[] Build(
        HarmonyUpdateBinLayout layout,
        (string Name, byte Type, byte[] Payload)[] components,
        HarmonyUpdateBinSignatureKind signature = HarmonyUpdateBinSignatureKind.Signature,
        IReadOnlyDictionary<string, byte[]>? digests = null,
        int truncateBytes = 0)
    {
        var nameLength = layout == HarmonyUpdateBinLayout.L1 ? 16 : 32;
        var recordLength = layout == HarmonyUpdateBinLayout.L1 ? 71 : 87;
        var table = new byte[components.Length * recordLength];
        using var payload = new MemoryStream();
        for (var index = 0; index < components.Length; index++)
        {
            var record = table.AsSpan(index * recordLength, recordLength);
            var name = Encoding.ASCII.GetBytes(components[index].Name);
            if (name.Length >= nameLength)
            {
                throw new InvalidOperationException("Fixture component names must leave a NUL terminator.");
            }

            name.CopyTo(record);
            record[nameLength + 4] = components[index].Type;
            BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(nameLength + 15, sizeof(ulong)),
                (ulong)components[index].Payload.Length);
            var digest = digests is not null && digests.TryGetValue(components[index].Name, out var overridden)
                ? overridden
                : Sha256.ComputeHash(components[index].Payload);
            if (digest.Length != 32)
            {
                throw new InvalidOperationException("Fixture digests must be 32 bytes.");
            }

            digest.CopyTo(record.Slice(nameLength + 23, 32));
            payload.Write(components[index].Payload);
        }

        if (table.Length is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("Fixture component table must fit the 16-bit length field.");
        }

        var header = new byte[180];
        BinaryPrimitives.WriteUInt16LittleEndian(header,
            layout == HarmonyUpdateBinLayout.L2 ? (ushort)0x01 : (ushort)0x11);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 136);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(176), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(178), (ushort)table.Length);

        var reserve = new byte[16];
        Encoding.ASCII.GetBytes("update/info.bin").CopyTo(reserve, 0);
        using var metadata = new MemoryStream();
        if (signature == HarmonyUpdateBinSignatureKind.HashAndSignature)
        {
            WriteTlv(metadata, 0x06, []);
            WriteTlv(metadata, 0x07, []);
            WriteTlv(metadata, 0x08, []);
        }
        else
        {
            WriteTlv(metadata, 0x08, new byte[32]);
        }

        using var result = new MemoryStream();
        result.Write(header);
        result.Write(table);
        result.Write(reserve);
        metadata.Position = 0;
        metadata.CopyTo(result);
        payload.Position = 0;
        payload.CopyTo(result);
        var bytes = result.ToArray();
        return truncateBytes == 0 ? bytes : bytes[..^truncateBytes];
    }

    private static void WriteTlv(Stream output, ushort kind, byte[] payload)
    {
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header, kind);
        BinaryPrimitives.WriteUInt32LittleEndian(header[2..], (uint)payload.Length);
        output.Write(header);
        output.Write(payload);
    }
}