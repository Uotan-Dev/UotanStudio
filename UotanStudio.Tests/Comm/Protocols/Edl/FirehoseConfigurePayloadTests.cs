using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class FirehoseConfigurePayloadTests
{
    [Theory]
    [InlineData(FirehoseStorageType.Nand, 64, true)]
    [InlineData(FirehoseStorageType.Nand, 0, false)]
    [InlineData(FirehoseStorageType.Emmc, 64, false)]
    [InlineData(FirehoseStorageType.Ufs, 64, false)]
    [InlineData(FirehoseStorageType.Spinor, 64, false)]
    public async Task ConfigureAsync_NandPagesPerBlock_EmitsAttributeOnlyForNandWhenSet(
        FirehoseStorageType storageType, long pagesPerBlock, bool expectAttribute)
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes("<data><response value='ACK'/></data>")];
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            StorageType = storageType,
            NandPagesPerBlock = pagesPerBlock,
            SectorSize = 4096,
            AutoDetectSectorSize = false
        });

        await protocol.ConfigureAsync(cancellationToken: TestContext.Current.CancellationToken);

        var configureFrame = FirstWrite(transport);
        Assert.Equal(expectAttribute, configureFrame.Contains("PAGES_PER_BLOCK=\"64\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgramAsync_MaxPayloadOverride_RaisesChunkBeyondLegacyCap()
    {
        using var transport = ProgramTransport();
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 2 * 1024 * 1024,
            MaxPayloadSizeToTargetSupported = 2 * 1024 * 1024,
            MaxProgramChunkBytes = 2 * 1024 * 1024
        });
        using var data = RawStream(2 * 1024 * 1024);

        await protocol.ProgramAsync(data, 0, 512, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(RawWrites(transport));
        Assert.Equal(2 * 1024 * 1024, RawWrites(transport)[0].Length);
    }

    [Fact]
    public async Task ProgramAsync_LegacyDefaultCap_SplitsChunkAtOneMebibyte()
    {
        using var transport = ProgramTransport();
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 2 * 1024 * 1024,
            MaxPayloadSizeToTargetSupported = 2 * 1024 * 1024
        });
        using var data = RawStream(2 * 1024 * 1024);

        await protocol.ProgramAsync(data, 0, 512, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, RawWrites(transport).Count);
        Assert.All(RawWrites(transport), write => Assert.Equal(1024 * 1024, write.Length));
    }

    [Fact]
    public async Task ProgramAsync_OverrideAboveNegotiated_ClampsToTargetPayload()
    {
        using var transport = ProgramTransport();
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 2 * 1024 * 1024,
            MaxPayloadSizeToTargetSupported = 8 * 1024 * 1024,
            MaxProgramChunkBytes = 8 * 1024 * 1024
        });
        using var data = RawStream(2 * 1024 * 1024);

        await protocol.ProgramAsync(data, 0, 512, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(RawWrites(transport));
        Assert.Equal(2 * 1024 * 1024, RawWrites(transport)[0].Length);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(513)]
    [InlineData(16777728)]
    [InlineData(0)]
    public async Task ProgramAsync_OverrideOutsideBound_FaultsWithoutWritingProgram(long chunkBound)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 2 * 1024 * 1024,
            MaxPayloadSizeToTargetSupported = 2 * 1024 * 1024,
            MaxProgramChunkBytes = chunkBound
        });
        using var data = RawStream(4096);

        await Assert.ThrowsAsync<EdlException>(() => protocol.ProgramAsync(data, 0, 1,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ProgramAsync_OverrideAboveSupported_FaultsBeforeWire()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 4 * 1024 * 1024,
            MaxPayloadSizeToTargetSupported = 2 * 1024 * 1024,
            MaxProgramChunkBytes = 4 * 1024 * 1024
        });
        using var data = RawStream(4096);

        await Assert.ThrowsAsync<EdlException>(() => protocol.ProgramAsync(data, 0, 1,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    private static ScriptedCommTransport ProgramTransport()
    {
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes => bytes.Length > 0 && bytes[0] == (byte)'<'
            ? [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>"),
               Encoding.UTF8.GetBytes("<data><response value='ACK'/></data>")]
            : [];
        return transport;
    }

    private static MemoryStream RawStream(int length)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, (byte)0xAB);
        return new MemoryStream(buffer, writable: false);
    }

    private static string FirstWrite(ScriptedCommTransport transport) => Encoding.UTF8.GetString(transport.Writes[0]);

    private static List<byte[]> RawWrites(ScriptedCommTransport transport) => [.. transport.Writes
        .Where(write => write.Length == 0 || write[0] != (byte)'<')];
}