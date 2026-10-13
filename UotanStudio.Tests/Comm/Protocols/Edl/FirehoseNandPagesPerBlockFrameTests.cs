using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class FirehoseNandPagesPerBlockFrameTests
{
    private const string Ready = "<data><response value=\"ACK\" rawmode=\"true\"/></data>";
    private const string Complete = "<data><response value=\"ACK\" rawmode=\"false\"/></data>";

    [Theory]
    [InlineData(FirehoseStorageType.Nand, 64, true)]
    [InlineData(FirehoseStorageType.Nand, 128, true)]
    [InlineData(FirehoseStorageType.Nand, 0, false)]
    [InlineData(FirehoseStorageType.Emmc, 64, false)]
    [InlineData(FirehoseStorageType.Ufs, 64, false)]
    [InlineData(FirehoseStorageType.Spinor, 64, false)]
    public async Task ProgramAsync_NandGeometry_EmitsPagesPerBlockOnlyOnNandFrames(FirehoseStorageType storageType,
        long pagesPerBlock, bool expectAttribute)
    {
        using var transport = ProgramTransport();
        using var protocol = CreateProtocol(transport, storageType, pagesPerBlock);
        using var data = new MemoryStream(new byte[512]);

        await protocol.ProgramAsync(data, 0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectAttribute,
            Frame(transport, "<program").Contains(Attribute(pagesPerBlock), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FirehoseStorageType.Nand, 64, true)]
    [InlineData(FirehoseStorageType.Nand, 0, false)]
    [InlineData(FirehoseStorageType.Emmc, 64, false)]
    [InlineData(FirehoseStorageType.Ufs, 64, false)]
    public async Task ReadToStreamAsync_NandGeometry_EmitsPagesPerBlockOnlyOnNandFrames(FirehoseStorageType storageType,
        long pagesPerBlock, bool expectAttribute)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ready);
        transport.Enqueue(new byte[512]);
        transport.Enqueue(Complete);
        using var protocol = CreateProtocol(transport, storageType, pagesPerBlock);
        using var destination = new MemoryStream();

        await protocol.ReadToStreamAsync(destination, 0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectAttribute,
            Frame(transport, "<read").Contains(Attribute(pagesPerBlock), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FirehoseStorageType.Nand, 64, true)]
    [InlineData(FirehoseStorageType.Nand, 0, false)]
    [InlineData(FirehoseStorageType.Emmc, 64, false)]
    [InlineData(FirehoseStorageType.Spinor, 64, false)]
    public async Task EraseAsync_NandGeometry_EmitsPagesPerBlockOnlyOnNandFrames(FirehoseStorageType storageType,
        long pagesPerBlock, bool expectAttribute)
    {
        using var transport = AckTransport();
        using var protocol = CreateProtocol(transport, storageType, pagesPerBlock);

        await protocol.EraseAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectAttribute,
            Frame(transport, "<erase").Contains(Attribute(pagesPerBlock), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FirehoseStorageType.Nand, 64, true)]
    [InlineData(FirehoseStorageType.Nand, 0, false)]
    [InlineData(FirehoseStorageType.Emmc, 64, false)]
    public async Task WipeAsync_NandGeometry_EmitsPagesPerBlockOnTheZerosProgramFrame(FirehoseStorageType storageType,
        long pagesPerBlock, bool expectAttribute)
    {
        using var transport = ProgramTransport();
        using var protocol = CreateProtocol(transport, storageType, pagesPerBlock);

        await protocol.WipeAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectAttribute,
            Frame(transport, "<program").Contains(Attribute(pagesPerBlock), StringComparison.Ordinal));
    }

    [Fact]
    public async Task EraseAsync_AttributeAboveIntRange_WritesTheReportedCountWithoutTruncation()
    {
        const long pagesPerBlock = int.MaxValue + 1L;
        using var transport = AckTransport();
        using var protocol = CreateProtocol(transport, FirehoseStorageType.Nand, pagesPerBlock);

        await protocol.EraseAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(Attribute(pagesPerBlock), Frame(transport, "<erase"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EraseAsync_NegativeGeometry_OmitsTheAttributeInsteadOfANegativeCount()
    {
        using var transport = AckTransport();
        using var protocol = CreateProtocol(transport, FirehoseStorageType.Nand, -1);

        await protocol.EraseAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("PAGES_PER_BLOCK", Frame(transport, "<erase"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EraseAsync_RelativeSlotConfigured_KeepsSlotAndPagesPerBlockOnTheSameFrame()
    {
        using var transport = AckTransport();
        using var protocol = CreateProtocol(transport, FirehoseStorageType.Nand, 64);
        protocol.Configuration.Slot = 2;

        await protocol.EraseAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken);

        var frame = Frame(transport, "<erase");
        Assert.Contains("slot=\"2\"", frame, StringComparison.Ordinal);
        Assert.Contains(Attribute(64), frame, StringComparison.Ordinal);
    }

    private static ScriptedCommTransport ProgramTransport()
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes => bytes.Length > 0 && bytes[0] == (byte)'<'
            ? [Encoding.UTF8.GetBytes(Ready), Encoding.UTF8.GetBytes(Complete)]
            : [];
        return transport;
    }

    private static ScriptedCommTransport AckTransport()
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(Complete)];
        return transport;
    }

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport, FirehoseStorageType storageType,
        long pagesPerBlock) => new(transport, new FirehoseConfiguration
        {
            StorageType = storageType,
            NandPagesPerBlock = pagesPerBlock,
            SectorSize = 512,
            AutoDetectSectorSize = false,
            ResponseTimeoutSeconds = 1
        });

    private static string Attribute(long pagesPerBlock) => $"PAGES_PER_BLOCK=\"{pagesPerBlock}\"";

    private static string Frame(ScriptedCommTransport transport, string element) => Encoding.UTF8.GetString(
        transport.Writes.First(write => Encoding.UTF8.GetString(write).Contains(element, StringComparison.Ordinal)));
}