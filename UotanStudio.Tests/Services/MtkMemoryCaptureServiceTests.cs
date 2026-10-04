using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Profile;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MtkMemoryCaptureServiceTests
{
    [Fact]
    public async Task CaptureAsync_XFlashBootRomWindow_PublishesByteExactImageAndRemovesStaging()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport);
        var expected = DaMemoryPattern(MtkDaMemoryAccess.DumpStepBytes * 2);
        EnqueueRegisterReplies(transport, expected);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "brom_8125.bin");
        var reports = new List<FlashProgress>();

        var record = await MtkMemoryCaptureService.CaptureAsync(session, MtkDaProtocol.XFlash,
            new MtkDaMemoryRange(0x0000_0000, MtkDaMemoryAccess.DumpStepBytes * 2), destination,
            new FlashListProgress(reports), TestContext.Current.CancellationToken);

        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal(MtkDaProtocol.XFlash, record.Family);
        Assert.Equal("BROM", record.RegionName);
        Assert.Equal(0x0000_0000u, record.StartAddress);
        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes * 2, record.RequestedBytes);
        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes, record.StepBytes);
        Assert.Equal(MtkDaMemoryAccess.DumpStepBytes * 2, record.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), record.Sha256Hex);
        Assert.Equal(destination, record.DestinationPath);
        Assert.Equal(100, reports[^1].Percent);
        Assert.Equal(["Capturing 0x80 bytes from 0x00000000 in 40-byte steps...", "Captured 64 of 128 bytes...",
            "Captured 128 of 128 bytes...",
            $"Published 128 bytes of the XFlash boot-ROM window with SHA-256 {record.Sha256Hex}."],
            [.. reports.Select(report => report.Status)]);
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Equal(6 * MtkDaMemoryAccess.DumpStepWords * 2, transport.Writes.Count - before);
    }

    [Fact]
    public async Task CaptureAsync_WholeDocumentedWindow_UsesDefaultOverloadAndRejectsMissingDirectory()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            MtkMemoryCaptureService.CaptureAsync(session, MtkDaProtocol.XFlash,
                Path.Combine(Path.GetTempPath(), "uotan-absent-" + Guid.NewGuid().ToString("N"), "brom.bin"), null,
                TestContext.Current.CancellationToken));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task CaptureAsync_WindowOutsideReferenceBootRomRange_RefusesBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkMemoryCaptureService.CaptureAsync(
            session, MtkDaProtocol.XFlash,
            new MtkDaMemoryRange(0x001F_FFC0, MtkDaMemoryAccess.MaximumDumpBytes),
            Path.Combine(directory.Path, "brom.bin"), null, TestContext.Current.CancellationToken));

        Assert.Contains("outside the reference boot-ROM capture range", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("XFlash")]
    [InlineData("Legacy")]
    [InlineData("Xml")]
    public async Task CaptureAsync_UnknownFamilyOrEmptyRange_RejectsBeforeDeviceIo(string family)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MtkMemoryCaptureService.CaptureAsync(
            session, (MtkDaProtocol)99, MtkDaMemoryRange.BromDump,
            Path.Combine(directory.Path, "brom.bin"), null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MtkMemoryCaptureService.CaptureAsync(
            session, Enum.Parse<MtkDaProtocol>(family), new MtkDaMemoryRange(0x0000_0000, 0),
            Path.Combine(directory.Path, "brom.bin"), null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => MtkMemoryCaptureService.CaptureAsync(
            session, Enum.Parse<MtkDaProtocol>(family), new MtkDaMemoryRange(0x0000_0000, 0x41),
            Path.Combine(directory.Path, "brom.bin"), null, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CaptureAsync_CancelledToken_WritesNothingAndKeepsDirectoryClean()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkMemoryCaptureService.CaptureAsync(
            session, MtkDaProtocol.XFlash, new MtkDaMemoryRange(0x0000_0000, MtkDaMemoryAccess.DumpStepBytes),
            Path.Combine(directory.Path, "brom.bin"), null, cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CaptureAsync_MissingReplyByte_KeepsExistingImageAndRemovesStaging()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport);
        EnqueueRegisterReplies(transport, DaMemoryPattern(MtkDaMemoryAccess.DumpStepBytes));
        var failure = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(failure, 0xC000_0001);
        EnqueueFrame(transport, failure);
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "brom_8125.bin");
        var existing = "PRE-EXISTING IMAGE"u8.ToArray();
        File.WriteAllBytes(destination, existing);

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkMemoryCaptureService.CaptureAsync(
            session, MtkDaProtocol.XFlash,
            new MtkDaMemoryRange(0x0000_0000, MtkDaMemoryAccess.DumpStepBytes * 2), destination, null,
            TestContext.Current.CancellationToken));

        Assert.Equal(existing, File.ReadAllBytes(destination));
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task CaptureAsync_CancelledMidCapture_LeavesExistingImageAndRemovesStaging()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXFlashSessionAsync(transport);
        EnqueueRegisterReplies(transport, DaMemoryPattern(MtkDaMemoryAccess.DumpStepBytes * 8));
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "brom_8125.bin");
        var existing = "PRE-EXISTING IMAGE"u8.ToArray();
        File.WriteAllBytes(destination, existing);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancellingProgress(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkMemoryCaptureService.CaptureAsync(
            session, MtkDaProtocol.XFlash,
            new MtkDaMemoryRange(0x0000_0000, MtkDaMemoryAccess.DumpStepBytes * 8), destination, progress,
            cancellation.Token));

        Assert.Equal(existing, File.ReadAllBytes(destination));
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ResolveBromDestinationPath_KnownHandshakeIdentity_UsesReferenceLowercaseName()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(
        [
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        using var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        using var directory = new TempDirectory();

        var path = MtkMemoryCaptureService.ResolveBromDestinationPath(session, directory.Path);

        Assert.Equal(Path.Combine(directory.Path, "brom_766.bin"), path);
        Assert.Equal(MtkDaMemoryAccess.GetBromDumpFileName(session.DeviceInfo!.HardwareCode),
            Path.GetFileName(path));
    }

    [Fact]
    public void ResolveBromDestinationPath_WithoutHandshakeIdentity_RefusesWithoutDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        using var directory = new TempDirectory();

        var error = Assert.Throws<InvalidOperationException>(() =>
            MtkMemoryCaptureService.ResolveBromDestinationPath(session, directory.Path));

        Assert.Contains("has not reported a hardware code", error.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    private static async Task<MtkSession> CreateXFlashSessionAsync(ScriptedCommTransport transport)
    {
        using var directory = new TempDirectory();
        var loaderPath = directory.WriteBytes("loader.bin", MtkSessionTests.BootstrapContainer());
        var profile = await MtkLoaderProfile.FromFileAsync(loaderPath, TestContext.Current.CancellationToken);
        MtkSessionTests.EnqueueBootstrap(transport, bootRom: false,
            geometry: MtkSessionTests.StorageGeometry(MtkXFlashStorageKind.Emmc));
        return await MtkBootstrap.MatchAsync(new MtkSession(transport), MtkDaProtocol.XFlash,
            new MtkAutoStartup { Profile = profile, StorageKind = MtkXFlashStorageKind.Emmc },
            TestContext.Current.CancellationToken);
    }

    private static void EnqueueRegisterReplies(ScriptedCommTransport transport, byte[] words)
    {
        for (var offset = 0; offset < words.Length; offset += MtkDaMemoryAccess.RegisterCellBytes)
        {
            EnqueueValue(transport, 0);
            EnqueueValue(transport, 0);
            EnqueueFrame(transport, words.AsSpan(offset, MtkDaMemoryAccess.RegisterCellBytes).ToArray());
            EnqueueValue(transport, 0);
        }
    }

    private static void EnqueueValue(ScriptedCommTransport transport, uint value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        EnqueueFrame(transport, payload);
    }

    private static void EnqueueFrame(ScriptedCommTransport transport, byte[] payload)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xFEEEEEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
        transport.Enqueue([.. header, .. payload]);
    }

    private static byte[] DaMemoryPattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index * 3 + 1);
        }

        return bytes;
    }

    private sealed class FlashListProgress(List<FlashProgress> reports) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => reports.Add(value);
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => cancellation.Cancel();
    }
}