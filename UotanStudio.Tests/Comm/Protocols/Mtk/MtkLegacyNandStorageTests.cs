using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyBootstrapAsync_NandInventory_ParsesSelectedWidthWithoutRequiringEmmc(bool size64)
    {
        using var transport = new ScriptedCommTransport();
        var capacity = size64 ? 8UL * 1024 * 1024 * 1024 : 8UL * 1024 * 1024;
        using var session = await CreateLegacyNandSessionAsync(transport, size64, capacity);

        var storage = Assert.IsType<MtkLegacyStorageInfo>(session.LegacyStorage);
        var nand = Assert.IsType<MtkLegacyNandGeometry>(storage.Nand);
        Assert.Equal(MtkXFlashStorageKind.Nand, storage.Kind);
        Assert.Null(storage.Nor);
        Assert.Empty(storage.Regions);
        Assert.Empty(storage.CidHex);
        Assert.Empty(storage.FirmwareRevisionHex);
        Assert.Equal((long)capacity, nand.CapacityBytes);
        Assert.Equal(2048, nand.PageSize);
        Assert.Equal(64, nand.SpareSize);
        Assert.Equal(131072, nand.EraseBlockSize);
        Assert.True(nand.HasBadBlockTable);
        Assert.Equal("1122", nand.DeviceIdsHex);
        Assert.Equal(Convert.ToHexString(LegacyNandInventory(size64, capacity)), nand.InventoryHex);
        Assert.Equal(Convert.ToHexString(LegacyNandDetails()), nand.DetailsHex);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("absent")]
    [InlineData("completion")]
    [InlineData("soc")]
    public async Task LegacyBootstrapAsync_NandFailure_DoesNotReturnAnEmmcSession(string failure)
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        var inventory = LegacyNandInventory(true);
        if (failure == "status")
        {
            inventory[3] = 1;
        }
        if (failure == "absent")
        {
            inventory.AsSpan(7, 8).Clear();
        }
        EnqueueLegacyBootstrap(transport, true, failure, nandInventory: inventory, nandDetails: LegacyNandDetails());

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy,
            LegacyBootstrapOptions(dir) with { StorageKind = MtkXFlashStorageKind.Nand, Legacy = LegacyProfile(true) },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, transport.DisposeCount);
    }

    [Theory]
    [InlineData("short-primary")]
    [InlineData("long-primary")]
    [InlineData("wrong-width")]
    [InlineData("short-details")]
    [InlineData("long-details")]
    [InlineData("status")]
    [InlineData("ids-truncated")]
    [InlineData("ids-excess")]
    [InlineData("id-count")]
    [InlineData("zero-page")]
    [InlineData("small-page")]
    [InlineData("non-power-page")]
    [InlineData("large-spare")]
    [InlineData("zero-block")]
    [InlineData("large-block")]
    [InlineData("zero-capacity")]
    [InlineData("unaligned-capacity")]
    [InlineData("overflow-capacity")]
    [InlineData("bad-bmt")]
    public void LegacyNandGeometry_InvalidInventory_Rejects(string failure)
    {
        var inventory = LegacyNandInventory(true);
        var details = LegacyNandDetails();
        byte[] ids = [0x11, 0x22];
        var size64 = true;
        switch (failure)
        {
            case "short-primary": inventory = inventory[..^1]; break;
            case "long-primary": inventory = [.. inventory, 0]; break;
            case "wrong-width": size64 = false; break;
            case "short-details": details = details[..^1]; break;
            case "long-details": details = [.. details, 0]; break;
            case "status": inventory[3] = 1; break;
            case "ids-truncated": ids = [0x11]; break;
            case "ids-excess": ids = [0x11, 0x22, 0x33, 0x44]; break;
            case "id-count": BinaryPrimitives.WriteUInt16BigEndian(inventory.AsSpan(15), 257); break;
            case "zero-page": details.AsSpan(0, 2).Clear(); break;
            case "small-page": BinaryPrimitives.WriteUInt16BigEndian(details, 256); break;
            case "non-power-page": BinaryPrimitives.WriteUInt16BigEndian(details, 3072); break;
            case "large-spare": BinaryPrimitives.WriteUInt16BigEndian(details.AsSpan(2), 2049); break;
            case "zero-block": details.AsSpan(4, 2).Clear(); break;
            case "large-block": BinaryPrimitives.WriteUInt16BigEndian(details.AsSpan(4), 65535); break;
            case "zero-capacity": inventory.AsSpan(7, 8).Clear(); break;
            case "unaligned-capacity": inventory[14] = 1; break;
            case "overflow-capacity": BinaryPrimitives.WriteUInt64BigEndian(inventory.AsSpan(7), 1UL << 63); break;
            case "bad-bmt": details[8] = 2; break;
        }

        Assert.Throws<MtkProtocolException>(() => MtkLegacyStorageInfo.ParseNand(inventory, ids, details, size64));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void LegacyNandGeometry_BoundedIdCounts_RetainsEveryByte(ushort count)
    {
        var inventory = LegacyNandInventory(false);
        BinaryPrimitives.WriteUInt16BigEndian(inventory.AsSpan(11), count);
        var ids = Enumerable.Range(0, count * 2).Select(index => (byte)index).ToArray();

        var storage = MtkLegacyStorageInfo.ParseNand(inventory, ids, LegacyNandDetails(), false);

        Assert.Equal(Convert.ToHexString(ids), storage.Nand!.DeviceIdsHex);
    }

    [Theory]
    [InlineData("emmc-read")]
    [InlineData("emmc-write")]
    [InlineData("emmc-erase")]
    [InlineData("nor-read")]
    [InlineData("nor-write")]
    public async Task LegacyStorageAsync_NandSession_RejectsOtherStorageCommandsBeforeIo(string operation)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var stream = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => operation switch
        {
            "emmc-read" => session.ReadLegacyStorageAsync(8, 0, 512, stream, cancellationToken: TestContext.Current.CancellationToken),
            "emmc-write" => session.WriteLegacyStorageAsync(8, 0, 512, stream, cancellationToken: TestContext.Current.CancellationToken),
            "emmc-erase" => session.EraseLegacyStorageAsync(8, 0, 512, cancellationToken: TestContext.Current.CancellationToken),
            "nor-read" => session.ReadLegacyNorStorageAsync(0, 512, stream, cancellationToken: TestContext.Current.CancellationToken),
            _ => session.WriteLegacyNorStorageAsync(0, 512, stream, cancellationToken: TestContext.Current.CancellationToken)
        });

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadLegacyNandStorageAsync_OtherStorageSession_RejectsBeforeIo(bool nor)
    {
        using var transport = new ScriptedCommTransport();
        using var session = nor
            ? await CreateLegacyNorSessionAsync(transport)
            : await CreateLegacyStorageSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadLegacyNandStorageAsync(0, 2048,
            destination, LegacyNandReadOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    private static MtkLegacyNandReadOptions LegacyNandReadOptions() => new()
    {
        Profile = MtkLegacyNandReadProfile.Sdk64Raw,
        AddressType = MtkLegacyNandAddressType.Physical,
        Mode = MtkLegacyNandReadMode.PageAndSpare,
        LengthUnit = MtkLegacyNandLengthUnit.MainAreaBytes
    };

    private static byte[] LegacyNandInventory(bool size64, ulong capacity = 8 * 1024 * 1024)
    {
        var inventory = new byte[size64 ? 17 : 13];
        inventory[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(inventory.AsSpan(5), 0x1234);
        if (size64)
        {
            BinaryPrimitives.WriteUInt64BigEndian(inventory.AsSpan(7), capacity);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(inventory.AsSpan(7), checked((uint)capacity));
        }
        inventory[^1] = 1;
        return inventory;
    }

    private static byte[] LegacyNandDetails() => Convert.FromHexString("080000400040000501");

    private static async Task<MtkSession> CreateLegacyNandSessionAsync(ScriptedCommTransport transport,
        bool size64 = true, ulong capacity = 8 * 1024 * 1024)
    {
        using var dir = new TempDirectory();
        var emmc = new byte[92];
        emmc[3] = 1;
        EnqueueLegacyBootstrap(transport, size64, emmcInventory: emmc,
            nandInventory: LegacyNandInventory(size64, capacity), nandDetails: LegacyNandDetails());
        return await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy,
            LegacyBootstrapOptions(dir) with
            {
                StorageKind = MtkXFlashStorageKind.Nand,
                Legacy = LegacyProfile(size64)
            }, TestContext.Current.CancellationToken);
    }
}