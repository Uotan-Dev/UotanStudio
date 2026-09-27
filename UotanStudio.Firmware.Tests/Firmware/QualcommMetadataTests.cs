using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Executable.Elf;
using UotanStudio.Firmware.BoundedContexts.Executable.Qualcomm;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class QualcommMetadataTests
{
    public static bool FirehoseSampleAvailable =>
        File.Exists(Environment.GetEnvironmentVariable("UOTAN_ELF_FIREHOSE_SAMPLE"));

    [Fact(Skip = "Set UOTAN_ELF_FIREHOSE_SAMPLE to the SM7150 DDR Firehose sample.", SkipUnless = nameof(FirehoseSampleAvailable))]
    public async Task AnalyzeAsync_Sm7150Firehose_ReadsDeclaredIdentityAndCertificates()
    {
        var result = await QualcommImageAnalyzer.AnalyzeAsync(Environment.GetEnvironmentVariable("UOTAN_ELF_FIREHOSE_SAMPLE")!,
            cancellationToken: TestContext.Current.CancellationToken);

        var metadata = Assert.Single(result.SigningMetadata, item => !item.FromCertificate && item.Authority == "OEM");
        Assert.Equal("PackedV6", metadata.Layout);
        Assert.Equal(0x1030, metadata.FileOffset);
        Assert.Equal(3UL, metadata.SoftwareId);
        Assert.Equal(0x72u, metadata.OemId);
        Assert.Equal(0x600cu, metadata.SocVersions[0]);
        Assert.Equal(3u, metadata.AntiRollbackVersion);
        Assert.Equal(0x102u, metadata.Flags);
        Assert.NotEmpty(result.Certificates);
        Assert.All(result.Certificates, cert => Assert.Equal(96, cert.Sha384.Length));
        Assert.Contains(result.VersionStrings, value => value.Name == "QcVersion"
            && value.Value == "BOOT.XF.3.1-00571-SM6150LZB-1.360815.1.402679.1");
        Assert.Contains(result.VersionStrings, value => value.Name == "ImageVariant" && value.Value == "SDM7150LA");
        Assert.Contains(result.VersionStrings, value => value.Name == "BuildTime" && value.Value == "2021-11-22 16:47:50");
        Assert.Contains(result.Certificates, value => value.Sha384 ==
            "1BEBE3863A6781DB4B01086063007334DE9E5CA14971C7C4F4358EC9D79CDA4692CE5E948C6FD409408F4C919FCADFE3");
        Assert.Equal(QualcommVerificationStatus.NotChecked, result.DeviceUsabilityStatus);
    }

    [Fact]
    public void Analyze_PackedV6DualMetadata_KeepsAuthoritiesAndDeclaredZeroValuesSeparate()
    {
        using var stream = new MemoryStream(QualcommMetadataFixture.CreateMbn(6, 120, 120));
        stream.Position = 7;

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.SigningMetadata.Count);
        var qti = result.SigningMetadata[0];
        var oem = result.SigningMetadata[1];
        Assert.Equal("QTI", qti.Authority);
        Assert.Equal(0x11u, qti.OemId);
        Assert.Equal("OEM", oem.Authority);
        Assert.Equal(0u, oem.OemId);
        Assert.Equal(168, oem.FileOffset);
        Assert.Equal(0x123456789abcdef0UL, oem.SerialNumbers[0]);
        Assert.Equal(12, oem.SocVersions.Count);
        Assert.Equal(4, oem.SerialNumbers.Count);
        Assert.Equal(3u, oem.AntiRollbackVersion);
        Assert.Equal(QualcommVerificationStatus.NotPresent, result.SignatureStatus);
        Assert.Equal(7, stream.Position);
        Assert.True(stream.CanRead);
        Assert.Contains(result.Regions, region => region.Name == "OemMetadata" && region.Offset == 168 && region.Length == 120);
    }

    [Fact]
    public void Analyze_TaggedV6ReorderedFields_UsesIdentifiersInsteadOfPackedOffsets()
    {
        var data = QualcommMetadataFixture.CreateMbn(6, 0, 168);
        var cursor = 48 + 8;
        for (uint id = 11; id > 0; id--)
        {
            QualcommMetadataFixture.Word(data, cursor, id);
            cursor += 4;
            var count = id switch { 8 => 12, 9 => 8, _ => 1 };
            for (var i = 0; i < count; i++)
            {
                QualcommMetadataFixture.Word(data, cursor, id == 10 ? 2u : id * 16 + (uint)i);
                cursor += 4;
            }
        }
        using var stream = new MemoryStream(data);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        var metadata = Assert.Single(result.SigningMetadata);
        Assert.Equal("TaggedV6", metadata.Layout);
        Assert.Equal(16UL, metadata.SoftwareId);
        Assert.Equal(32UL, metadata.HardwareId);
        Assert.Equal(64u, metadata.SoftwareSize);
        Assert.Equal(80u, metadata.ModelId);
        Assert.Equal(2u, metadata.RootCertificateSlot);
        Assert.Equal(176u, metadata.AntiRollbackVersion);
        Assert.Equal(8, metadata.SerialNumbers.Count);
        Assert.Equal(151UL, metadata.SerialNumbers[^1]);
        Assert.Equal(16u, result.Mbn!.SoftwareId);
    }

    [Theory]
    [InlineData(8, 99u)]
    [InlineData(0, 1u)]
    public void Analyze_UnknownTaggedFieldOrMetadataVersion_PreservesVersionWithoutInventingIdentity(int offset, uint value)
    {
        var data = QualcommMetadataFixture.CreateMbn(6, 0, 168);
        QualcommMetadataFixture.Word(data, 48 + offset, value);
        using var stream = new MemoryStream(data);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        var metadata = Assert.Single(result.SigningMetadata);
        Assert.Equal("UnsupportedMetadata", metadata.Layout);
        Assert.Null(metadata.SoftwareId);
        Assert.Null(metadata.RootCertificateSlot);
        Assert.Null(result.Mbn!.SoftwareId);
    }

    [Fact]
    public void Analyze_V7CommonAndDualMetadata_UsesDeclaredLengthsAndRetainsRawRootHash()
    {
        using var stream = new MemoryStream(QualcommMetadataFixture.CreateMbn(7, 16, 224));

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.SigningMetadata.Count);
        Assert.Equal("Common", result.SigningMetadata[0].Authority);
        Assert.Equal(0x35UL, result.SigningMetadata[0].SoftwareId);
        Assert.Equal(0x99u, result.SigningMetadata[0].SecondarySoftwareId);
        Assert.Equal(72, result.SigningMetadata[1].FileOffset);
        var oem = result.SigningMetadata[2];
        Assert.Equal(88, oem.FileOffset);
        Assert.Equal(0x72u, oem.OemId);
        Assert.Equal(0x13u, oem.ModelId);
        Assert.Equal(0x600cu, oem.SocVersions[0]);
        Assert.Equal(8, oem.SerialNumbers.Count);
        Assert.Equal(0x123456789abcdef0UL, oem.SerialNumbers[0]);
        Assert.Equal(128, oem.RootHash!.Length);
        Assert.StartsWith("AB", oem.RootHash);
        Assert.Equal(2u, oem.RootHashAlgorithm);
        Assert.Equal(312, result.Mbn!.HashOffset);
    }

    [Fact]
    public void Analyze_ZeroFilledMetadata_DoesNotPresentPlaceholderAsDeclaredIdentity()
    {
        var data = QualcommMetadataFixture.CreateMbn(6, 0, 120);
        data.AsSpan(48, 120).Clear();
        using var stream = new MemoryStream(data);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        var metadata = Assert.Single(result.SigningMetadata);
        Assert.Equal("ZeroFilledMetadata", metadata.Layout);
        Assert.Null(metadata.SoftwareId);
        Assert.Null(metadata.OemId);
        Assert.Empty(metadata.SerialNumbers);
    }

    [Fact]
    public void Analyze_CertificateOuDeclarations_Preserves64BitsDuplicatesAndCertificateDetails()
    {
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName("Metadata, not a device assertion");
        name.AddOrganizationalUnitName("01 0000000300000003 SW_ID");
        name.AddOrganizationalUnitName("02 1234567800720001 HW_ID");
        name.AddOrganizationalUnitName("04 0072 OEM_ID");
        name.AddOrganizationalUnitName("06 0001 MODEL_ID");
        name.AddOrganizationalUnitName("06 0002 MODEL_ID");
        name.AddOrganizationalUnitName("11 600C 600D SOC_VERS");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var mbn = QualcommMetadataFixture.CreateMbn(5, 0, 0);
        var data = new byte[mbn.Length + certificate.RawData.Length];
        mbn.CopyTo(data, 0);
        certificate.RawData.CopyTo(data, mbn.Length);
        QualcommMetadataFixture.Word(data, 16, (uint)(data.Length - 40));
        QualcommMetadataFixture.Word(data, 36, (uint)certificate.RawData.Length);
        using var stream = new MemoryStream(data);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        var metadata = Assert.Single(result.SigningMetadata);
        Assert.True(metadata.FromCertificate);
        Assert.Equal(0x300000003UL, metadata.SoftwareId);
        Assert.Equal(3u, metadata.AntiRollbackVersion);
        Assert.Equal(0x1234567800720001UL, metadata.HardwareId);
        Assert.Equal(0x72u, metadata.OemId);
        Assert.Null(metadata.ModelId);
        Assert.Equal(new uint[] { 0x600c, 0x600d }, metadata.SocVersions);
        var summary = Assert.Single(result.Certificates);
        Assert.Equal(2, summary.Attributes.Count(field => field.Name == "MODEL_ID"));
        Assert.Equal(Convert.ToHexString(SHA384.HashData(certificate.RawData)), summary.Sha384);
        Assert.Equal(certificate.SerialNumber, summary.SerialNumber);
        Assert.Equal("1.2.840.113549.1.1.1", summary.PublicKeyAlgorithmOid);
        Assert.Equal(QualcommVerificationStatus.NotPresent, result.SignatureStatus);
    }

    [Fact]
    public void Analyze_VersionStringAcrossReadBoundary_ReportsStaticEvidenceAndOffset()
    {
        var payload = new byte[70_000];
        "QC_IMAGE_VERSION_STRING=TEST.1.2\0"u8.CopyTo(payload.AsSpan(65520));
        "OEM_IMAGE_VERSION_STRING=OEM.3\0"u8.CopyTo(payload.AsSpan(66000));
        using var stream = new MemoryStream(QualcommMetadataFixture.CreateElf(payload));

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.VersionStrings.Count);
        Assert.Contains(result.VersionStrings, item => item.Name == "QcVersion" && item.Value == "TEST.1.2"
            && item.FileOffset == 512 + 65520);
        Assert.Equal(QualcommVerificationStatus.NotChecked, result.DeviceUsabilityStatus);
    }

    [Theory]
    [InlineData("unterminated")]
    [InlineData("tooLong")]
    [InlineData("invalidUtf8")]
    public void Analyze_InvalidVersionLiteral_DoesNotPublishMisleadingValue(string kind)
    {
        var prefix = "QC_IMAGE_VERSION_STRING="u8.ToArray();
        var payload = kind switch
        {
            "unterminated" => prefix.Concat("missing terminator"u8.ToArray()).ToArray(),
            "tooLong" => prefix.Concat(Enumerable.Repeat((byte)'A', 1024)).Append((byte)0).ToArray(),
            _ => prefix.Concat(new byte[] { 0xff, 0 }).ToArray()
        };
        using var stream = new MemoryStream(QualcommMetadataFixture.CreateElf(payload));

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.VersionStrings);
    }

    [Theory]
    [InlineData("Nov 22 2021", "16:47:50", true)]
    [InlineData("Feb 31 2021", "16:47:50", false)]
    [InlineData("Nov 22 2021", "garbage", false)]
    public void Analyze_AdjacentBuildDateStrings_RequiresValidDateAndTime(string date, string time, bool valid)
    {
        var payload = Encoding.ASCII.GetBytes($"Binary build date: %s @ %s\0{date}\0{time}\0");
        using var stream = new MemoryStream(QualcommMetadataFixture.CreateElf(payload));

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(valid, result.VersionStrings.Any(value => value.Name == "BuildTime"));
        if (valid)
        {
            Assert.Equal("2021-11-22 16:47:50", Assert.Single(result.VersionStrings).Value);
        }
    }

    [Fact]
    public void Read_VersionScanLimit_DoesNotReadEntireLargeSegment()
    {
        using var stream = new ZeroStream(1024L * 1024 * 1024);
        var segment = new ElfSegment(0, 1, 4, 0, 0, 0, (ulong)stream.Length, (ulong)stream.Length, 1);

        var result = QualcommVersionScanner.Read(stream, [segment], TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(16 * 1024 * 1024, stream.BytesRead);
        Assert.InRange(stream.MaximumRead, 1, 65536);
    }

    [Fact]
    public void IsQualcommElf_ValidAndTruncatedHashSegment_RestoresPositionAndRejectsTruncation()
    {
        var data = QualcommMetadataFixture.CreateElf(new byte[16], QualcommMetadataFixture.CreateMbn(6, 0, 120));
        using var stream = new MemoryStream(data);
        stream.Position = 13;

        Assert.True(QualcommImageProbe.IsQualcommElf(stream, TestContext.Current.CancellationToken));
        Assert.Equal(13, stream.Position);
        using var truncated = new MemoryStream(data[..^1]);
        Assert.False(QualcommImageProbe.IsQualcommElf(truncated, TestContext.Current.CancellationToken));
        Assert.Equal(0, truncated.Position);
    }

    [Fact]
    public void Analyze_CanceledMetadataRead_RestoresPositionWithoutPublishing()
    {
        using var stream = new MemoryStream(QualcommMetadataFixture.CreateMbn(6, 0, 120));
        stream.Position = 19;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => QualcommImageAnalyzer.Analyze(stream,
            cancellationToken: cancellation.Token));
        Assert.Equal(19, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(6u, 120)]
    [InlineData(7u, 224)]
    public void Analyze_TruncatedDeclaredMetadata_RejectsBeforePublishingFields(uint version, int length)
    {
        var image = QualcommMetadataFixture.CreateMbn(version, 0, length);
        using var stream = new MemoryStream(image[..^1]);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Format);
        Assert.Empty(result.SigningMetadata);
        Assert.Empty(result.Regions);
        Assert.NotEmpty(result.Diagnostics);
    }

    private sealed class ZeroStream(long length) : Stream
    {
        public int BytesRead { get; private set; }
        public int MaximumRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - Position);
            buffer[..count].Clear();
            BytesRead += count;
            MaximumRead = Math.Max(MaximumRead, count);
            Position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static class QualcommMetadataFixture
{
    internal static byte[] CreateMbn(uint version, int qtiSize, int oemSize)
    {
        var header = version == 6 ? 48 : 40;
        var common = version == 7 ? 32 : 0;
        var data = new byte[header + common + qtiSize + oemSize + 96];
        Word(data, 0, 5);
        Word(data, 4, version);
        Word(data, 20, 96);
        if (version == 7)
        {
            Word(data, 8, (uint)common);
            Word(data, 12, (uint)qtiSize);
            Word(data, 16, (uint)oemSize);
            Word(data, 48, 0x35);
            Word(data, 52, 0x99);
            Word(data, 56, 3);
        }
        else
        {
            Word(data, 16, (uint)(data.Length - header));
            if (version == 6)
            {
                Word(data, 40, (uint)qtiSize);
                Word(data, 44, (uint)oemSize);
            }
        }
        Metadata(header + common, qtiSize, 0x11);
        Metadata(header + common + qtiSize, oemSize, 0);
        return data;

        void Metadata(int offset, int size, uint oem)
        {
            if (version == 6 && size == 120)
            {
                Word(data, offset + 8, 3);
                Word(data, offset + 16, oem);
                Word(data, offset + 32, 0x600c);
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 80), 0x123456789abcdef0UL);
                Word(data, offset + 116, 3);
            }
            else if (version == 7 && size >= 16)
            {
                Word(data, offset + 8, 3);
                if (size == 224)
                {
                    Word(data, offset + 16, 0x600c);
                    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset + 72), 0x123456789abcdef0UL);
                    Word(data, offset + 136, 0x72);
                    Word(data, offset + 140, 0x13);
                    Word(data, offset + 152, 2);
                    data[offset + 156] = 0xab;
                }
            }
        }
    }

    internal static byte[] CreateElf(byte[] payload, byte[]? mbn = null)
    {
        var hashOffset = 512 + payload.Length;
        var data = new byte[hashOffset + (mbn?.Length ?? 0)];
        "\u007fELF"u8.CopyTo(data);
        data[4] = 2;
        data[5] = data[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 183);
        Word(data, 20, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(56), mbn is null ? (ushort)1 : (ushort)2);
        Word(data, 64, 1);
        Word(data, 68, 5);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(72), 512);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(80), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(96), (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(104), (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(112), 1);
        payload.CopyTo(data, 512);
        if (mbn is not null)
        {
            Word(data, 124, 0x02200000);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(128), (ulong)hashOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(152), (ulong)mbn.Length);
            mbn.CopyTo(data, hashOffset);
        }
        return data;
    }

    internal static void Word(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
}