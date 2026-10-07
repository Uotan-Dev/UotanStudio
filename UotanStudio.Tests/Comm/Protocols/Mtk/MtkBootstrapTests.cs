using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapInitializeAsync_CompleteScript_ReachesStorageReadyWithSelectedBytes(bool bootRom)
    {
        using var dir = new TempDirectory();
        var options = BootstrapOptions(dir);
        if (bootRom)
        {
            using var block = new MemoryStream("MTK_BLOADER_INFO_v12parameters"u8.ToArray());
            options = options with
            {
                EmiParameters = MtkEmiReader.ReadBlock(block, 0, (int)block.Length,
                    MtkEmiPayloadLayout.BootloaderInfo, TestContext.Current.CancellationToken)
            };
        }
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, bootRom);

        using var session = await MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.NotNull(session.XFlashStorage);
        Assert.True(transport.IsConnected);
        Assert.Contains(transport.Writes, bytes => bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Contains(transport.Writes, bytes => bytes.AsSpan().SequenceEqual(Enumerable.Repeat((byte)0x42, 12).ToArray()));
        Assert.DoesNotContain(transport.Writes, bytes => bytes.AsSpan().SequenceEqual(new byte[] { 0xEE, 0xEE, 0xEE, 0xEE }));
        using var exclusive = new FileStream(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("same-region")]
    [InlineData("unaligned")]
    [InlineData("empty")]
    [InlineData("invalid-index")]
    public async Task BootstrapInitializeAsync_InvalidResources_DisposesWithoutDeviceIo(string failure)
    {
        using var dir = new TempDirectory();
        var bytes = BootstrapContainer();
        var options = BootstrapOptions(dir);
        switch (failure)
        {
            case "same-region": options = options with { StageTwoRegionIndex = 0 }; break;
            case "unaligned": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(136), 0x200001); break;
            case "empty":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(132), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(144), 0);
                break;
            case "invalid-index": options = options with { EntryIndex = 1 }; break;
        }
        dir.WriteBytes("loader.bin", bytes);
        using var transport = new ScriptedCommTransport();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(1, transport.DisposeCount);
        using var exclusive = new FileStream(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task BootstrapInitializeAsync_V6HeaderedLoader_IsAccepted()
    {
        using var dir = new TempDirectory();
        var bytes = BootstrapContainer();
        "MTK_DA_v6"u8.CopyTo(bytes.AsSpan(32));
        var options = BootstrapOptions(dir);
        dir.WriteBytes("loader.bin", bytes);
        using var transport = new ScriptedCommTransport();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken));

        Assert.Equal(new byte[] { 0xA0 }, transport.Writes.Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapInitializeAsync_RevisionPolicy_IsExplicit(bool minimum)
    {
        using var dir = new TempDirectory();
        var bytes = BootstrapContainer();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(114), 0xCA00);
        var options = BootstrapOptions(dir) with
        {
            IdentityMatch = minimum ? MtkDaIdentityMatch.MinimumRevisions : MtkDaIdentityMatch.Exact
        };
        dir.WriteBytes("loader.bin", bytes);
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, false);

        if (minimum)
        {
            using var session = await MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken);
            Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken));
            Assert.Equal(11, transport.Writes.Count);
            Assert.Equal(1, transport.DisposeCount);
        }
    }

    [Fact]
    public async Task BootstrapInitializeAsync_BootRomWithoutEmi_StopsBeforeDa2()
    {
        using var dir = new TempDirectory();
        var options = BootstrapOptions(dir);
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(transport.Writes, bytes => bytes.AsSpan().SequenceEqual(Convert.FromHexString("08000100")));
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task BootstrapInitializeAsync_AuthorizationRejected_PreservesStatusAndClosesTransport()
    {
        using var dir = new TempDirectory();
        var options = BootstrapOptions(dir);
        using var transport = new ScriptedCommTransport();
        EnqueueIdentity(transport);
        transport.Enqueue(Convert.FromHexString("D70020000000000008000000021D0D"));

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() =>
            MtkXFlashBootstrap.InitializeAsync(transport, options, TestContext.Current.CancellationToken));

        Assert.Equal(0x1D0Du, error.Status);
        Assert.Equal(1, transport.DisposeCount);
        Assert.Equal(15, transport.Writes.Count);
    }

    [Fact]
    public async Task BootstrapInitializeAsync_PreCancelled_DisposesOwnedTransportWithoutIo()
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MtkXFlashBootstrap.InitializeAsync(transport, BootstrapOptions(dir), cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task BootstrapMatchAsync_IdentityFromHandshake_SelectsEntryAndReachesStorageReady()
    {
        using var dir = new TempDirectory();
        var loaderPath = dir.WriteBytes("loader.bin", BootstrapContainer());
        var profile = await MtkLoaderProfile.FromFileAsync(loaderPath, TestContext.Current.CancellationToken);
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, bootRom: false);

        using var session = await MtkBootstrap.MatchAsync(transport, MtkDaProtocol.XFlash,
            new MtkAutoStartup { Profile = profile, StorageKind = MtkXFlashStorageKind.Emmc },
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.NotNull(session.XFlashStorage);
        Assert.Contains(transport.Writes, bytes => bytes.AsSpan().SequenceEqual(Enumerable.Repeat((byte)0x42, 12).ToArray()));
    }

    [Fact]
    public async Task BootstrapMatchAsync_ContainerIdentityMismatch_StopsBeforeDaUpload()
    {
        using var dir = new TempDirectory();
        var bytes = BootstrapContainer();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(110), 0x9999);
        var loaderPath = dir.WriteBytes("loader.bin", bytes);
        var profile = await MtkLoaderProfile.FromFileAsync(loaderPath, TestContext.Current.CancellationToken);
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, bootRom: false);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => MtkBootstrap.MatchAsync(transport,
            MtkDaProtocol.XFlash, new MtkAutoStartup { Profile = profile, StorageKind = MtkXFlashStorageKind.Emmc },
            TestContext.Current.CancellationToken));

        Assert.Contains("did not resolve to one entry", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Writes, bytes => bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public async Task BootstrapMatchAsync_BorrowedLeasedSession_ReachesStorageReadyOnSameSession()
    {
        using var dir = new TempDirectory();
        var loaderPath = dir.WriteBytes("loader.bin", BootstrapContainer());
        var profile = await MtkLoaderProfile.FromFileAsync(loaderPath, TestContext.Current.CancellationToken);
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, bootRom: false);
        using var session = new MtkSession(transport);

        var booted = await MtkBootstrap.MatchAsync(session, MtkDaProtocol.XFlash,
            new MtkAutoStartup { Profile = profile, StorageKind = MtkXFlashStorageKind.Emmc },
            TestContext.Current.CancellationToken);

        Assert.Same(session, booted);
        Assert.Equal(MtkSessionState.XFlashStorageReady, booted.State);
        Assert.NotNull(booted.XFlashStorage);
    }

    private static MtkXFlashBootstrapOptions BootstrapOptions(TempDirectory dir) => new()
    {
        LoaderPath = dir.WriteBytes("loader.bin", BootstrapContainer()),
        EntryIndex = 0,
        StageOneRegionIndex = 0,
        StageTwoRegionIndex = 1,
        StageTwoContent = MtkDaRegionContent.WithoutSignatureSuffix,
        StorageKind = MtkXFlashStorageKind.Emmc
    };

    internal static byte[] BootstrapContainer()
    {
        var bytes = new byte[1048];
        "MTK_DOWNLOAD_AGENT"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100), 0x22668899);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104), 1);
        ushort[] entry = [0xDADA, 0x766, 0x8A00, 0xCA01, 2, 0, 4096, 0, 0, 2];
        for (var index = 0; index < entry.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(108 + index * 2), entry[index]);
        }
        uint[] regions = [1024, 8, 0x200000, 0, 2, 1032, 16, 0x40000000, 12, 4];
        for (var index = 0; index < regions.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128 + index * 4), regions[index]);
        }
        for (var index = 0; index < 8; index++)
        {
            bytes[1024 + index] = (byte)(index + 1);
        }
        bytes.AsSpan(1032, 12).Fill(0x42);
        bytes.AsSpan(1044, 4).Fill(0xEE);
        return bytes;
    }

    internal static void EnqueueBootstrap(ScriptedCommTransport transport, bool bootRom,
        MtkBootResourceKind[]? resources = null, bool requireSla = false, bool requireDaSla = false,
        byte[]? geometry = null)
    {
        EnqueueIdentity(transport);
        foreach (var kind in resources ?? [])
        {
            EnqueueBootResource(transport, kind, 8, 0, 0x0800, 0);
        }
        if (requireSla)
        {
            transport.Enqueue(Convert.FromHexString("D70020000000000008000000021D0D"));
            EnqueueSlaExchange(transport);
            transport.Enqueue(Convert.FromHexString("08000000D5002000000000"));
        }
        else
        {
            transport.Enqueue(Convert.FromHexString("D7002000000000000800000002000008000000D5002000000000"));
        }
        EnqueueXFlashSetup(transport);
        EnqueueControlReply(transport, bootRom ? "brom"u8.ToArray() : "preloader"u8.ToArray());
        if (bootRom)
        {
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
        }
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0x434E5953);
        EnqueueControlReply(transport, requireDaSla ? [1, 0, 0, 0] : new byte[4]);
        if (requireDaSla)
        {
            EnqueueControlReply(transport, new byte[20]);
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
        }
        EnqueueControlReply(transport, geometry ?? StorageGeometry(MtkXFlashStorageKind.Emmc));
        EnqueueControlReply(transport, PacketLengths());
    }
}