using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public sealed class FlashBundleDigestBindingTests
{
    private const int SectorSize = 4096;
    private const int Sectors = 4;
    private const long StartSector = 16384;
    private const int RangeBytes = SectorSize * Sectors;
    private const long NegotiatedPayload = 1048576;
    private static readonly string Signature = Convert.ToBase64String(new byte[64]);

    private const string AckXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <data>
        <response value="ACK" rawmode="false"/>
        </data>
        """;

    private const string ConfigureAckXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <data>
        <response value="ACK" rawmode="false" MaxPayloadSizeToTargetInBytes="1048576" Program="true" Erase="true" Patch="true" Wipe="true"/>
        </data>
        """;

    private const string NopResponseXml = """
        <data><log value="booted"/></data><data><response value="ACK"/></data>
        """;

    private const string StorageInfoXml = """
        <data><log value='{"storage_info":{"block_size":4096,"total_blocks":1048576}}'/><response value="ACK"/></data>
        """;

    [Fact]
    public async Task TryPrepareAsync_NullDigestSignature_RefusesBeforeHashingAnySourceByte()
    {
        var image = Image();
        using var source = new MemoryStream(image);
        var entry = ManifestEntry(DigestOf(image));
        entry.DigestSignature = null;

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            entry, source, SectorSize, Sectors, StartSector, NegotiatedPayload, null,
            TestContext.Current.CancellationToken);

        Assert.False(prepared);
        Assert.Null(command);
        Assert.Equal(FlashBundleDigestCommand.MissingSignatureReason, refusal);
        Assert.Equal(0L, source.Position);
    }

    [Fact]
    public async Task TryPrepareAsync_EmptyDigestSignature_RefusesBeforeHashingAnySourceByte()
    {
        var image = Image();
        using var source = new MemoryStream(image);
        var entry = ManifestEntry(DigestOf(image));
        entry.DigestSignature = string.Empty;

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            entry, source, SectorSize, Sectors, StartSector, NegotiatedPayload, null,
            TestContext.Current.CancellationToken);

        Assert.False(prepared);
        Assert.Null(command);
        Assert.Equal(FlashBundleDigestCommand.MissingSignatureReason, refusal);
        Assert.Equal(0L, source.Position);
    }

    [Fact]
    public async Task TryPrepareAsync_SignatureSizeBeyondIntRange_RefusesBeforeHashingAnySourceByte()
    {
        var image = Image();
        using var source = new MemoryStream(image);
        var entry = ManifestEntry(DigestOf(image));
        entry.SignatureSize = long.MaxValue;

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            entry, source, SectorSize, Sectors, StartSector, NegotiatedPayload, null,
            TestContext.Current.CancellationToken);

        Assert.False(prepared);
        Assert.Null(command);
        Assert.Equal(FlashBundleDigestCommand.MissingSignatureReason, refusal);
        Assert.Equal(0L, source.Position);
    }

    [Fact]
    public async Task TryPrepareAsync_SmallWriteChunks_DigestMatchesSha256OfEveryWireByte()
    {
        var image = Image();
        using var source = new MemoryStream(image);

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            ManifestEntry(DigestOf(image)), source, SectorSize, Sectors, StartSector, SectorSize, null,
            TestContext.Current.CancellationToken);

        Assert.True(prepared);
        Assert.Null(refusal);
        Assert.NotNull(command);
        var expected = SHA256.HashData(image);
        Assert.Equal(DigestOf(image), command!.DigestHex);
        Assert.Equal(expected, command.Digest);
        Assert.Equal((long)RangeBytes, command.ByteCount);
        Assert.Equal(4, command.PacketCount);
    }

    [Fact]
    public async Task TryPrepareAsync_SourceShortInsideTheLastSector_HashesTheZeroFilledTail()
    {
        var image = Image();
        var dataBytes = image[..(RangeBytes - 384)];
        var padded = new byte[RangeBytes];
        dataBytes.CopyTo(padded, 0);
        using var source = new MemoryStream(dataBytes);

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            ManifestEntry(DigestOf(padded)), source, SectorSize, Sectors, StartSector, RangeBytes / 2, null,
            TestContext.Current.CancellationToken);

        Assert.True(prepared);
        Assert.Null(refusal);
        Assert.NotNull(command);
        Assert.Equal(DigestOf(padded), command!.DigestHex);
        Assert.Equal(SHA256.HashData(padded), command.Digest);
        Assert.Equal((long)RangeBytes, command.ByteCount);
        Assert.Equal(2, command.PacketCount);
        Assert.Equal(0L, source.Position);
    }

    [Fact]
    public async Task TryPrepareAsync_SourceShortByOneWholeSector_RefusesAsOutOfRangeWithoutConsumingTheSource()
    {
        using var source = new MemoryStream(Image()[..(RangeBytes - SectorSize)]);

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            ManifestEntry(DigestOf(Image())), source, SectorSize, Sectors, StartSector, NegotiatedPayload, null,
            TestContext.Current.CancellationToken);

        Assert.False(prepared);
        Assert.Null(command);
        Assert.Equal(FlashBundleDigestCommand.RangeReason, refusal);
        Assert.Equal(0L, source.Position);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-4096L)]
    [InlineData(4000L)]
    [InlineData(16777217L)]
    public async Task TryPrepareAsync_UnusableWriteChunk_RefusesAsOutOfRange(long payloadChunkBytes)
    {
        var image = Image();
        using var source = new MemoryStream(image);

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            ManifestEntry(DigestOf(image)), source, SectorSize, Sectors, StartSector, payloadChunkBytes, null,
            TestContext.Current.CancellationToken);

        Assert.False(prepared);
        Assert.Null(command);
        Assert.Equal(FlashBundleDigestCommand.RangeReason, refusal);
    }

    [Fact]
    public async Task TryPrepareAsync_ManifestDeclaresUppercaseHexDigest_AcceptsTheSameByteSequence()
    {
        var image = Image();
        using var source = new MemoryStream(image);

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            ManifestEntry(DigestOf(image).ToUpperInvariant()), source, SectorSize, Sectors, StartSector,
            NegotiatedPayload, null, TestContext.Current.CancellationToken);

        Assert.True(prepared);
        Assert.Null(refusal);
        Assert.NotNull(command);
        Assert.Equal(DigestOf(image), command!.DigestHex);
        Assert.Contains($"digest=\"{DigestOf(image)}\"", command.CommandXml, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveChunkBytes_NegotiatedPayloadBelowChunkBound_UsesThePayload()
    {
        var chunk = FlashBundleDigestCommand.EffectiveChunkBytes(new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 8192,
            MaxProgramChunkBytes = 1048576
        });

        Assert.Equal(8192L, chunk);
    }

    [Fact]
    public void EffectiveChunkBytes_ChunkBoundBelowThePayload_RoundsDownToWholeSectors()
    {
        var chunk = FlashBundleDigestCommand.EffectiveChunkBytes(new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 1048576,
            MaxProgramChunkBytes = 10000
        });

        Assert.Equal(8192L, chunk);
    }

    [Fact]
    public void EffectiveChunkBytes_SmallSectorSize_KeepsTheFullNegotiatedPayload()
    {
        var chunk = FlashBundleDigestCommand.EffectiveChunkBytes(new FirehoseConfiguration
        {
            SectorSize = 512,
            MaxPayloadSizeToTarget = 1048576,
            MaxProgramChunkBytes = 1048576
        });

        Assert.Equal(1048576L, chunk);
    }

    [Fact]
    public void EffectiveChunkBytes_ChunkBoundBelowOneSector_Throws()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => FlashBundleDigestCommand.EffectiveChunkBytes(
            new FirehoseConfiguration
            {
                SectorSize = 4096,
                MaxPayloadSizeToTarget = 2048,
                MaxProgramChunkBytes = 2048
            }));

        Assert.Contains("smaller than one sector", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveChunkBytes_ConfigurationWithoutSectorSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FlashBundleDigestCommand.EffectiveChunkBytes(
            new FirehoseConfiguration
            {
                SectorSize = 0,
                MaxPayloadSizeToTarget = 1048576,
                MaxProgramChunkBytes = 1048576
            }));
    }

    [Fact]
    public async Task FlashBundleAsync_DigestBoundProgram_PayloadBytesHashToTheDeclaredDigest()
    {
        var image = Image();
        var digest = DigestOf(image);
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", Manifest(digest));
        dir.WriteBytes("vbmeta.img", image);
        using var transport = CreateTransport(digest);
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(rawPath, patchPath: null,
            dir.Path, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        var payload = transport.Writes
            .Where(static write => write.Length != 0 && write[0] != (byte)'<')
            .SelectMany(static write => write)
            .ToArray();
        Assert.Equal((long)RangeBytes, (long)payload.Length);
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(payload)));
        var programXml = Encoding.UTF8.GetString(Assert.Single(transport.Writes,
            static write => write.Length > 0 && write[0] == (byte)'<'
                && Encoding.UTF8.GetString(write).Contains("<program ", StringComparison.Ordinal)));
        Assert.Contains("filename=\"vbmeta.img\"", programXml, StringComparison.Ordinal);
        Assert.Contains("start_sector=\"16384\"", programXml, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"4\"", programXml, StringComparison.Ordinal);
        var digestQuery = Encoding.UTF8.GetString(Assert.Single(transport.Writes,
            static write => write.Length > 0 && write[0] == (byte)'<'
                && Encoding.UTF8.GetString(write).Contains("<getsha256digest", StringComparison.Ordinal)));
        Assert.Contains("start_sector=\"16384\"", digestQuery, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"4\"", digestQuery, StringComparison.Ordinal);
        Assert.Contains("SECTOR_SIZE_IN_BYTES=\"4096\"", digestQuery, StringComparison.Ordinal);
        var evidence = Assert.Single(result.Items, static item => item.Kind == FlashBundleItemKind.Program);
        Assert.Contains($"digest=\"{digest}\"", evidence.CommandXml!, StringComparison.Ordinal);
        Assert.Contains("signature_size=\"64\"", evidence.CommandXml!, StringComparison.Ordinal);
        Assert.Equal(1, result.ProgramEntriesProcessed);
        Assert.Equal((long)RangeBytes, result.TotalBytesWritten);
    }

    private static string Manifest(string digest) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <program SECTOR_SIZE_IN_BYTES="4096" filename="vbmeta.img" physical_partition_number="0"
                   start_sector="16384" num_partition_sectors="4" label="vbmeta"
                   digest="{{digest}}" digest_signature="{{Signature}}" signature_size="64" />
        </data>
        """;

    private static RawProgramEntry ManifestEntry(string digest) => new()
    {
        SectorSizeInBytes = SectorSize,
        NumPartitionSectors = Sectors,
        Filename = "vbmeta.img",
        PhysicalPartitionNumber = 0,
        StartSector = "16384",
        Label = "vbmeta",
        Digest = digest,
        DigestSignature = Signature,
        SignatureSize = 64
    };

    private static byte[] Image()
    {
        var image = new byte[RangeBytes];
        for (var index = 0; index < image.Length; index++)
        {
            image[index] = (byte)((index % 233) + 1);
        }

        return image;
    }

    private static string DigestOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ScriptedCommTransport CreateTransport(string declaredDigestHex) => new()
    {
        OnWrite = bytes =>
        {
            if (bytes.Length == 0 || bytes[0] != (byte)'<')
            {
                return [];
            }

            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<getsha256digest", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(
                    $"<data><log value='Digest {declaredDigestHex}'/><response value='ACK' rawmode='false'/></data>")];
            }

            if (text.Contains("<getstorageinfo", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(StorageInfoXml)];
            }

            if (text.Contains("<nop", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(NopResponseXml)];
            }

            if (text.Contains("<configure", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(ConfigureAckXml)];
            }

            if (text.Contains("<program ", StringComparison.Ordinal))
            {
                return
                [
                    Encoding.UTF8.GetBytes("<data><response value=\"ACK\" rawmode=\"true\"/></data>"),
                    Encoding.UTF8.GetBytes(AckXml)
                ];
            }

            return [Encoding.UTF8.GetBytes(AckXml)];
        }
    };

    private static EdlSession CreateSession(ScriptedCommTransport transport) => new(transport, new FirehoseConfiguration
    {
        SectorSize = SectorSize,
        AutoDetectSectorSize = false
    });
}