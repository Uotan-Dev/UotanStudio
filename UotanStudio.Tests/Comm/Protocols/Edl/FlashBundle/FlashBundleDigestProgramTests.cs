using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public class FlashBundleDigestProgramTests
{
    private const int SectorSize = 4096;
    private const int Sectors = 4;
    private const long StartSector = 32768;
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
    public void BuildProgramWithDigest_ValidBinding_EmitsExactDigestBoundDocument()
    {
        var xml = FirehoseXmlBuilder.BuildProgramWithDigest(SectorSize, Sectors, "boot.img", 0, StartSector,
            new string('0', 64), Signature, 64, "boot", null);

        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n"
            + "<program SECTOR_SIZE_IN_BYTES=\"4096\" num_partition_sectors=\"4\" filename=\"boot.img\""
            + " physical_partition_number=\"0\" start_sector=\"32768\" label=\"boot\""
            + $" digest=\"{new string('0', 64)}\" digest_signature=\"{Signature}\" signature_size=\"64\" />"
            + "\n</data>\n", xml);
    }

    [Fact]
    public void BuildProgramWithDigest_DeclaredSizeBelowDecodedLength_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FirehoseXmlBuilder.BuildProgramWithDigest(
            SectorSize, Sectors, "boot.img", 0, StartSector, new string('0', 64), Signature, 63));
    }

    [Fact]
    public void BuildProgramWithDigest_MalformedBase64_ThrowsBeforeProducingText()
    {
        Assert.Throws<FormatException>(() => FirehoseXmlBuilder.BuildProgramWithDigest(
            SectorSize, Sectors, "boot.img", 0, StartSector, new string('0', 64), "not base64!!", 64));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789")]
    [InlineData("ZZ2345678901234567890123456789012345678901234567890123456789012345")]
    public void BuildProgramWithDigest_DigestNotOneSha256Width_Throws(string digest)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FirehoseXmlBuilder.BuildProgramWithDigest(
            SectorSize, Sectors, "boot.img", 0, StartSector, digest, Signature, 64));
    }

    [Fact]
    public void RawProgramParser_DigestBoundProgramEntry_KeepsLiteralDigestAttributes()
    {
        using var dir = new TempDirectory();
        var path = dir.Write("rawprogram0.xml", Manifest(DigestOf(Image())));

        var entry = Assert.Single(RawProgramParser.Parse(path));

        Assert.True(entry.IsDigestBound);
        Assert.Equal(DigestOf(Image()), entry.Digest);
        Assert.Equal(Signature, entry.DigestSignature);
        Assert.Equal(64L, entry.SignatureSize);
    }

    [Fact]
    public void EffectiveChunkBytes_NegotiatedPayloadAndChunkBound_TakesTheSmallerSectorAlignedValue()
    {
        var chunk = FlashBundleDigestCommand.EffectiveChunkBytes(new FirehoseConfiguration
        {
            SectorSize = 4096,
            MaxPayloadSizeToTarget = 1048576,
            MaxProgramChunkBytes = 65536
        });

        Assert.Equal(65536L, chunk);
    }

    [Fact]
    public async Task TryPrepareAsync_DeclaredDigestOfSentBytes_BuildsDocumentOverWholeRange()
    {
        var image = Image();
        using var source = new MemoryStream(image);
        source.Position = 0;

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            ManifestEntry(DigestOf(image)), source, SectorSize, Sectors, StartSector, 1048576, null,
            TestContext.Current.CancellationToken);

        Assert.True(prepared);
        Assert.Null(refusal);
        Assert.NotNull(command);
        Assert.Equal(16384L, command.ByteCount);
        Assert.Equal(1, command.PacketCount);
        Assert.Equal(DigestOf(image), command.DigestHex);
        Assert.Contains($"digest=\"{DigestOf(image)}\"", command.CommandXml, StringComparison.Ordinal);
        Assert.Contains("digest_signature=", command.CommandXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryPrepareAsync_OneWireByteChanged_RefusesBecauseDigestIsBoundToSentBytes()
    {
        var image = Image();
        var declared = DigestOf(image);
        image[7] ^= 0x01;
        using var source = new MemoryStream(image);

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            ManifestEntry(declared), source, SectorSize, Sectors, StartSector, 1048576, null,
            TestContext.Current.CancellationToken);

        Assert.False(prepared);
        Assert.Null(command);
        Assert.Equal(FlashBundleDigestCommand.DigestMismatchReason, refusal);
    }

    [Fact]
    public async Task TryPrepareAsync_PartiallyFilledLastSector_HashesTheZeroPaddedTail()
    {
        var image = Image();
        var truncated = image[..(image.Length - 10)];
        var padded = new byte[image.Length];
        truncated.CopyTo(padded, 0);
        using var source = new MemoryStream(truncated);

        var (prepared, command, _) = await FlashBundleDigestCommand.TryPrepareAsync(
            ManifestEntry(DigestOf(padded)), source, SectorSize, Sectors, StartSector, 1048576, null,
            TestContext.Current.CancellationToken);

        Assert.True(prepared);
        Assert.NotNull(command);
        Assert.Equal(DigestOf(padded), command.DigestHex);
    }

    [Fact]
    public async Task FlashBundleAsync_DigestBoundProgramMatch_QuestsTargetDigestAfterWrite()
    {
        var image = Image();
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", Manifest(DigestOf(image)));
        dir.WriteBytes("boot.img", image);
        using var transport = CreateTransport(DigestOf(image));
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(rawPath, patchPath: null,
            dir.Path, progress: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.ProgramEntriesProcessed);
        Assert.Contains(Commands(transport), static command => command.Name == "getsha256digest");
        var evidence = Assert.Single(result.Items, static item => item.Kind == FlashBundleItemKind.Program);
        Assert.NotNull(evidence.CommandXml);
        Assert.Contains($"digest=\"{DigestOf(image)}\"", evidence.CommandXml, StringComparison.Ordinal);
        Assert.Contains("signature_size=\"64\"", evidence.CommandXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlashBundleAsync_DigestBoundProgramMismatch_EmitsZeroProgramWrites()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", Manifest(new string('a', 64)));
        dir.WriteBytes("boot.img", Image());
        using var transport = CreateTransport(new string('a', 64));
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(rawPath, patchPath: null,
            dir.Path, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.ProgramEntriesProcessed);
        Assert.Equal(0L, result.TotalBytesWritten);
        Assert.DoesNotContain(Commands(transport), static command => command.Name == "program");
        Assert.DoesNotContain(transport.Writes, static write => write.Length == 16384);
        Assert.Contains(result.Errors, static error => error.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlashBundleAsync_TargetReportsDifferentDigest_FailsTheItemAfterTheWrite()
    {
        var image = Image();
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", Manifest(DigestOf(image)));
        dir.WriteBytes("boot.img", image);
        using var transport = CreateTransport(new string('f', 64));
        using var session = CreateSession(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(rawPath, patchPath: null,
            dir.Path, progress: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.ProgramEntriesProcessed);
        var evidence = Assert.Single(result.Items, static item => item.Kind == FlashBundleItemKind.Program);
        Assert.Equal(CommandOutcomeKind.Rejected, evidence.Outcome);
        Assert.Equal(EdlFailureKind.Rejected, evidence.FailureKind);
        Assert.Contains(result.Errors, static error => error.Contains("does not match the declared write digest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryPrepareAsync_NegativeStartSector_RefusesWithoutBuildingDocument()
    {
        var image = Image();
        using var source = new MemoryStream(image);

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            ManifestEntry(DigestOf(image)), source, SectorSize, Sectors, -1, 1048576, null,
            TestContext.Current.CancellationToken);

        Assert.False(prepared);
        Assert.Null(command);
        Assert.Equal(FlashBundleDigestCommand.RangeReason, refusal);
    }

    [Fact]
    public async Task TryPrepareAsync_SignatureWithoutDeclaredSize_RefusesBeforeHashing()
    {
        var image = Image();
        using var source = new MemoryStream(image);
        var entry = ManifestEntry(DigestOf(image));
        entry.SignatureSize = 0;

        var (prepared, command, refusal) = await FlashBundleDigestCommand.TryPrepareAsync(
            entry, source, SectorSize, Sectors, StartSector, 1048576, null,
            TestContext.Current.CancellationToken);

        Assert.False(prepared);
        Assert.Null(command);
        Assert.Equal(FlashBundleDigestCommand.MissingSignatureReason, refusal);
        Assert.Equal(0L, source.Position);
    }

    private static string Manifest(string digest) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <program SECTOR_SIZE_IN_BYTES="4096" filename="boot.img" physical_partition_number="0"
                   start_sector="32768" num_partition_sectors="4" label="boot"
                   digest="{{digest}}" digest_signature="{{Signature}}" signature_size="64" />
        </data>
        """;

    private static RawProgramEntry ManifestEntry(string digest) => new()
    {
        SectorSizeInBytes = SectorSize,
        NumPartitionSectors = Sectors,
        Filename = "boot.img",
        StartSector = "32768",
        Label = "boot",
        Digest = digest,
        DigestSignature = Signature,
        SignatureSize = 64
    };

    private static byte[] Image()
    {
        var image = new byte[SectorSize * Sectors];
        for (var index = 0; index < image.Length; index++)
        {
            image[index] = (byte)(index % 251);
        }

        return image;
    }

    private static string DigestOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static IEnumerable<System.Xml.Linq.XElement> Commands(ScriptedCommTransport transport)
    {
        foreach (var write in transport.Writes)
        {
            if (write.Length == 0 || write[0] != (byte)'<')
            {
                continue;
            }

            var root = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(write)).Root;
            foreach (var element in root!.Elements())
            {
                yield return element;
            }
        }
    }

    private static EdlSession CreateSession(ScriptedCommTransport transport)
        => new(transport, new FirehoseConfiguration
        {
            SectorSize = SectorSize,
            AutoDetectSectorSize = false
        });

    private static ScriptedCommTransport CreateTransport(string reportedDigestHex) => new()
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
                    $"<data><log value='Digest {reportedDigestHex}'/><response value='ACK' rawmode='false'/></data>")];
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
}