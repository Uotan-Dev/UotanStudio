using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    internal static void ValidateLegacyEmi(MtkEmiParameters? emi)
    {
        if (emi is null)
        {
            return;
        }
        if (emi.Length is <= 0 or > 4 * 1024 * 1024
            || emi.Version is not (0 or 0xA or 0xB or 0xC or 0xD or 0xF or 0x10 or 0x11 or 0x14 or 0x15))
        {
            throw new NotSupportedException("The selected Legacy EMI resource has an unsupported version or length.");
        }
    }

    private async Task InitializeLegacyDramOwnedAsync(MtkEmiParameters? emi, CancellationToken token)
    {
        _ = await ReadAsync(20, _timeoutMs, token).ConfigureAwait(false);
        var indication = await ReadLegacyWordAsync(token).ConfigureAwait(false);
        if (indication != 0xBC4)
        {
            throw new MtkProtocolException("The Legacy DRAM request lacks its NAND-info marker.", indication);
        }
        await ReadLegacyNandIdsAsync(token).ConfigureAwait(false);
        if (emi is null)
        {
            throw new InvalidOperationException("The Legacy agent requires an explicitly selected DRAM resource.");
        }
        var request = new byte[5];
        request[0] = 0xE8;
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(1), emi.Version == 0 ? uint.MaxValue : (uint)emi.Version);
        await WriteAsync(request, _timeoutMs, token).ConfigureAwait(false);
        await RequireLegacyByteAsync(0x5A, "DRAM version", token).ConfigureAwait(false);
        if (emi.Version is 0xA or 0xB)
        {
            _ = await ReadAsync(16, _timeoutMs, token).ConfigureAwait(false);
        }
        var requested = await ReadLegacyWordAsync(token).ConfigureAwait(false);
        if (requested is 0 or > 4 * 1024 * 1024)
        {
            throw new MtkProtocolException("The Legacy DRAM request length is outside the supported limit.");
        }
        var truncate = emi.Version is 0 or 0xC or 0xD;
        if ((truncate && requested > emi.Length) || (emi.Version is 0xA or 0xB && requested != emi.Length))
        {
            throw new MtkProtocolException("The selected Legacy DRAM resource cannot satisfy the requested length.");
        }
        var length = truncate ? checked((int)requested) : emi.Length;
        if (emi.Version is 0xC or 0xD && length < 4)
        {
            throw new MtkProtocolException("The Legacy DRAM resource is too short for its parameter prefix.");
        }
        await WriteAsync([0x5A], _timeoutMs, token).ConfigureAwait(false);
        if (emi.Version == 0 || (emi.Version is 0xF or 0x10 or 0x11 or 0x14 or 0x15 && DeviceInfo!.HardwareCode != 0x8127))
        {
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)length);
            await WriteAsync(size, _timeoutMs, token).ConfigureAwait(false);
        }
        using var source = emi.OpenRead();
        var payload = new byte[length];
        await source.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        if (emi.Version is 0xC or 0xD)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload, 0x100);
        }
        await WriteAsync(payload, _timeoutMs, token).ConfigureAwait(false);
        LegacyDramReportedChecksum = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(2, _timeoutMs, token).ConfigureAwait(false));
        await WriteAsync([0x5A, 0x80, 0x00, 0x00, 0x01], _timeoutMs, token).ConfigureAwait(false);
        var result = await ReadLegacyWordAsync(token).ConfigureAwait(false);
        if (result != 0)
        {
            throw new MtkProtocolException("Legacy external RAM initialization failed.", result);
        }
        var geometry = await ReadAsync(10, _timeoutMs, token).ConfigureAwait(false);
        if (geometry[0] != 2 || BinaryPrimitives.ReadUInt64BigEndian(geometry.AsSpan(2)) == 0)
        {
            throw new MtkProtocolException("The Legacy agent did not report nonempty external DRAM.");
        }
        if (emi.Version == 0xD)
        {
            _ = await ReadAsync(20, _timeoutMs, token).ConfigureAwait(false);
        }
    }
}