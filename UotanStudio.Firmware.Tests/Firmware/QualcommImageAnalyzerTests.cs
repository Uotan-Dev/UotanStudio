using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UotanStudio.Firmware.BoundedContexts.Executable.Qualcomm;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class QualcommImageAnalyzerTests
{
    [Fact]
    public void Analyze_ConcatenatedElfComponents_EnumeratesBoundedMelfMembers()
    {
        var data = new byte[256];
        WriteElfHeader(data.AsSpan(0), 0xB7);
        WriteElfHeader(data.AsSpan(128), 0xF3);
        using var stream = new MemoryStream(data);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.MelfComponents.Count);
        Assert.Equal(new QualcommMelfComponent(0, 128, true, 0xB7), result.MelfComponents[0]);
        Assert.Equal(new QualcommMelfComponent(128, 128, true, 0xF3), result.MelfComponents[1]);
    }

    [Fact]
    public void Analyze_ConcatenatedElfComponents_CancellationStopsScan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var data = new byte[256];
        WriteElfHeader(data.AsSpan(0), 0xB7);
        WriteElfHeader(data.AsSpan(128), 0xF3);
        using var stream = new MemoryStream(data);

        Assert.Throws<OperationCanceledException>(() =>
            QualcommMelfComponents.Scan(stream, cancellation.Token));
    }

    private static void WriteElfHeader(Span<byte> data, ushort machine)
    {
        data.Clear();
        "\u007fELF"u8.CopyTo(data);
        data[4] = 2;
        data[5] = 1;
        data[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data[16..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data[18..], machine);
        BinaryPrimitives.WriteUInt32LittleEndian(data[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data[52..], 64);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Analyze_ShortInput_DoesNotReadUninitializedPrefix(int length)
    {
        var data = new byte[length];
        if (length >= 4)
        {
            "\u007fELF"u8.CopyTo(data);
        }

        using var stream = new MemoryStream(data);
        stream.Position = length;

        var result = QualcommImageAnalyzer.Analyze(stream,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Format);
        Assert.Equal(length, result.FileSize);
        Assert.Equal(length, stream.Position);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Analyze_ShortMbnVersionPrefix_DoesNotGuessFormat(int length)
    {
        var data = new byte[length];
        if (length > 4)
        {
            data[4] = 5;
        }

        using var stream = new MemoryStream(data);
        var result = QualcommImageAnalyzer.Analyze(stream,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Format);
        Assert.Equal(length, result.FileSize);
    }

    [Fact]
    public void Analyze_SignedElfWithCallerRoot_SeparatesIntegritySignatureTrustAndDevice()
    {
        var fixture = SignedImage.Create();
        using var stream = new MemoryStream(fixture.Elf);
        stream.Position = 12;

        var result = QualcommImageAnalyzer.Analyze(stream, fixture.Trust(), TestContext.Current.CancellationToken);

        Assert.Equal(QualcommImageFormat.Elf, result.Format);
        Assert.Equal(5u, result.Mbn!.Version);
        Assert.Equal(QualcommVerificationStatus.Valid, result.HashStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.CertificateChainStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.TrustedRootStatus);
        Assert.Equal(QualcommVerificationStatus.NotPresent, result.QualcommSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.NotChecked, result.DeviceUsabilityStatus);
        Assert.Equal(2, result.SegmentDigests.Count);
        Assert.Equal(2, result.Certificates.Count);
        Assert.Equal(fixture.Elf.Length, result.FileSize);
        Assert.Equal(12, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Analyze_MissingOrWrongCallerRoot_DoesNotInventTrust()
    {
        var fixture = SignedImage.Create();
        using var stream = new MemoryStream(fixture.Elf);

        var missing = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);
        var wrong = QualcommImageAnalyzer.Analyze(stream, new()
        {
            TrustedRootCertificateSha256 = [new string('0', 64)]
        }, TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Valid, missing.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.NotChecked, missing.TrustedRootStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, wrong.OemSignature.TrustedRootStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, wrong.OemSignature.CertificateChainStatus);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(7u)]
    public void Analyze_Sha384CallerRoot_ValidatesExactDerFingerprint(uint version)
    {
        var fixture = SignedImage.Create().WithHeaderVersion(version);
        using var stream = new MemoryStream(fixture.Mbn);
        var options = fixture.TrustSha384();

        var result = QualcommImageAnalyzer.Analyze(stream, options, TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.CertificateChainStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.TrustedRootStatus);
        Assert.Equal(options.TrustedRootCertificateSha384.Single().ToUpperInvariant(),
            result.OemSignature.RootCertificateSha384);
        Assert.Equal(QualcommVerificationStatus.NotChecked, result.DeviceUsabilityStatus);
    }

    [Fact]
    public void Analyze_WrongSha384CallerRoot_DoesNotTrustValidSignature()
    {
        var fixture = SignedImage.Create();
        using var stream = new MemoryStream(fixture.Elf);
        var options = new QualcommAnalysisOptions
        {
            TrustedRootCertificateSha384 = [new string('0', 96)]
        };

        var result = QualcommImageAnalyzer.Analyze(stream, options, TestContext.Current.CancellationToken);
        var alternate = QualcommImageAnalyzer.Analyze(stream,
            options with { TrustedRootCertificateSha256 = fixture.Trust().TrustedRootCertificateSha256 },
            TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, result.OemSignature.TrustedRootStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, alternate.OemSignature.TrustedRootStatus);
    }

    [Fact]
    public void Analyze_Sha384CallerRootWithInvalidMetadata_DoesNotOverrideFailure()
    {
        var fixture = SignedImage.Create().WithHeaderVersion(7, rootSlot: 0,
            metadataHash: true, corruptRootDigest: true);
        using var stream = new MemoryStream(fixture.Mbn);

        var result = QualcommImageAnalyzer.Analyze(stream, fixture.TrustSha384(),
            TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, result.MetadataRootHashStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, result.OemSignature.TrustedRootStatus);
    }

    [Fact]
    public void Analyze_LegacyMbnSignatureMismatch_ReportsUnsupportedHmacVariant()
    {
        var fixture = SignedImage.Create().WithHeaderVersion(5);
        fixture.Mbn[104] ^= 1;
        using var stream = new MemoryStream(fixture.Mbn);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QualcommImageFormat.Mbn, result.Format);
        Assert.Equal(QualcommVerificationStatus.Unsupported, result.OemSignature.SignatureStatus);
        Assert.Null(result.OemSignature.SignatureAlgorithm);
        Assert.Contains(result.Diagnostics, value =>
            value.Contains("legacy Qualcomm HMAC", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(QualcommVerificationStatus.NotChecked, result.DeviceUsabilityStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a fingerprint")]
    [InlineData(null)]
    public void Analyze_MalformedSha384CallerRoot_RejectsBeforeReading(string? fingerprint)
    {
        using var stream = new MemoryStream([0, 1, 2, 3]);
        stream.Position = 2;

        Assert.Throws<ArgumentException>(() => QualcommImageAnalyzer.Analyze(stream,
            new() { TrustedRootCertificateSha384 = [fingerprint!] },
            TestContext.Current.CancellationToken));

        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void Analyze_DataAndHashTamper_FailOnlyAffectedLayers()
    {
        var fixture = SignedImage.Create();
        var dataTampered = (byte[])fixture.Elf.Clone();
        dataTampered[0x200] ^= 0x10;
        var hashTampered = (byte[])fixture.Elf.Clone();
        hashTampered[0x400 + 40] ^= 0x10;

        using var data = new MemoryStream(dataTampered);
        using var hash = new MemoryStream(hashTampered);
        var dataResult = QualcommImageAnalyzer.Analyze(data, fixture.Trust(), TestContext.Current.CancellationToken);
        var hashResult = QualcommImageAnalyzer.Analyze(hash, fixture.Trust(), TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Invalid, dataResult.HashStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, dataResult.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, dataResult.TrustedRootStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, hashResult.HashStatus);
        Assert.Equal(QualcommVerificationStatus.Unsupported, hashResult.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, hashResult.CertificateChainStatus);
    }

    [Fact]
    public void Analyze_StandaloneMbn_DoesNotClaimElfHashIntegrity()
    {
        var fixture = SignedImage.Create();
        using var stream = new MemoryStream(fixture.Mbn);

        var result = QualcommImageAnalyzer.Analyze(stream, fixture.Trust(), TestContext.Current.CancellationToken);

        Assert.Equal(QualcommImageFormat.Mbn, result.Format);
        Assert.Equal(QualcommVerificationStatus.NotChecked, result.HashStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.TrustedRootStatus);
    }

    [Theory]
    [InlineData(3u, "SHA-256")]
    [InlineData(6u, "SHA-384")]
    [InlineData(7u, "SHA-384")]
    public void Analyze_SignedHeaderVersions_VerifiesIndependentDigestAndSignature(uint version, string algorithm)
    {
        var fixture = SignedImage.Create().WithHeaderVersion(version);
        using var stream = new MemoryStream(fixture.Elf);

        var result = QualcommImageAnalyzer.Analyze(stream, fixture.Trust(), TestContext.Current.CancellationToken);

        Assert.Equal(version, result.Mbn!.Version);
        Assert.Equal(algorithm, result.Mbn.HashAlgorithm);
        Assert.Equal(QualcommVerificationStatus.Valid, result.HashStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.TrustedRootStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Analyze_TamperedCertificate_DoesNotTrustEmbeddedRoot(bool sha384)
    {
        var fixture = SignedImage.Create();
        var image = (byte[])fixture.Elf.Clone();
        image[^1] ^= 1;
        using var stream = new MemoryStream(image);

        var result = QualcommImageAnalyzer.Analyze(stream, sha384 ? fixture.TrustSha384() : fixture.Trust(),
            TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Invalid, result.CertificateChainStatus);
        Assert.NotEqual(QualcommVerificationStatus.Valid, result.TrustedRootStatus);
    }

    [Fact]
    public void Analyze_NullSha384Collection_RejectsBeforeReading()
    {
        using var stream = new MemoryStream([0, 1, 2, 3]);
        stream.Position = 2;

        Assert.Throws<ArgumentException>(() => QualcommImageAnalyzer.Analyze(stream,
            new() { TrustedRootCertificateSha384 = null! },
            TestContext.Current.CancellationToken));

        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void Analyze_UnsupportedV7Algorithm_DoesNotVerifyDigest()
    {
        var fixture = SignedImage.Create().WithHeaderVersion(7);
        var image = (byte[])fixture.Elf.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x400 + 56), 99);
        using var stream = new MemoryStream(image);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Unsupported, result.HashStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, result.OemSignature.SignatureStatus);
    }

    [Fact]
    public void Analyze_V7SelectedRootSlotMismatch_DoesNotAcceptOtherEmbeddedRoot()
    {
        var fixture = SignedImage.Create().WithHeaderVersion(7, rootSlot: 1);
        using var stream = new MemoryStream(fixture.Elf);

        var result = QualcommImageAnalyzer.Analyze(stream, fixture.Trust(), TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Valid, result.HashStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, result.OemSignature.CertificateChainStatus);
        Assert.NotEqual(QualcommVerificationStatus.Valid, result.OemSignature.TrustedRootStatus);
    }

    [Fact]
    public void Analyze_V7SignedMetadataRootDigestMismatch_IsNotPromotedToTrust()
    {
        var fixture = SignedImage.Create().WithHeaderVersion(7, rootSlot: 0, metadataHash: true,
            corruptRootDigest: true);
        using var stream = new MemoryStream(fixture.Elf);

        var result = QualcommImageAnalyzer.Analyze(stream, fixture.Trust(), TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.CertificateChainStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, result.MetadataRootHashStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, result.OemSignature.TrustedRootStatus);
    }

    [Fact]
    public void Analyze_V7SignedMetadataRootDigestMatch_ValidatesIndependently()
    {
        var fixture = SignedImage.Create().WithHeaderVersion(7, rootSlot: 0, metadataHash: true);
        using var stream = new MemoryStream(fixture.Elf);

        var result = QualcommImageAnalyzer.Analyze(stream, fixture.Trust(), TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Valid, result.MetadataRootHashStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, result.OemSignature.TrustedRootStatus);
    }

    [Fact]
    public void Analyze_SblMbn_SignedPrefixAndCodeVerify()
    {
        var fixture = SignedImage.Create();
        var image = fixture.CreateSbl();
        using var stream = new MemoryStream(image);

        var valid = QualcommImageAnalyzer.Analyze(stream, fixture.Trust(), TestContext.Current.CancellationToken);
        image[80] ^= 1;
        using var tampered = new MemoryStream(image);
        var invalid = QualcommImageAnalyzer.Analyze(tampered, fixture.Trust(), TestContext.Current.CancellationToken);

        Assert.Equal(QualcommImageFormat.SblMbn, valid.Format);
        Assert.Equal(QualcommVerificationStatus.NotPresent, valid.HashStatus);
        Assert.Equal(QualcommVerificationStatus.Valid, valid.OemSignature.SignatureStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, invalid.OemSignature.SignatureStatus);
    }

    [Fact]
    public void Analyze_MalformedLengthsAndCancelledStream_DoNotConsumeOrAllocateDeclaredRanges()
    {
        var fixture = SignedImage.Create();
        var malformed = (byte[])fixture.Mbn.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(36), uint.MaxValue);
        using var stream = new MemoryStream(malformed);
        stream.Position = 17;

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.Format);
        Assert.Equal(17, stream.Position);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => QualcommImageAnalyzer.Analyze(
            stream, cancellationToken: cancellation.Token));
        Assert.Equal(17, stream.Position);
    }

    [Fact]
    public void Analyze_MalformedRootFingerprint_RejectsBeforeAnalysis()
    {
        using var stream = new MemoryStream(SignedImage.Create().Elf);
        Assert.Throws<ArgumentException>(() => QualcommImageAnalyzer.Analyze(stream,
            new() { TrustedRootCertificateSha256 = ["not a fingerprint"] },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Analyze_ProgrammerStrings_ReportsEvidenceNotDeviceCapability()
    {
        var fixture = SignedImage.Create();
        var image = (byte[])fixture.Elf.Clone();
        "program\0read\0configure\0"u8.CopyTo(image.AsSpan(0x200));
        using var stream = new MemoryStream(image);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.SupportedCommands, command => command.Name == "program" && command.Source == "StringLiteral");
        Assert.Equal(QualcommVerificationStatus.NotChecked, result.DeviceUsabilityStatus);
        Assert.Equal(QualcommVerificationStatus.Invalid, result.HashStatus);
    }

    [Fact]
    public void Analyze_MappedHandlerTable_PromotesOnlyAddressValidatedEntries()
    {
        var fixture = SignedImage.Create();
        var image = (byte[])fixture.Elf.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(96), 256);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(104), 256);
        "program\0read\0configure\0"u8.CopyTo(image.AsSpan(0x200));
        var names = new ulong[] { 0x2000, 0x2008, 0x200d };
        for (var i = 0; i < names.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x250 + i * 16), names[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x258 + i * 16), 0x2080);
        }

        using var stream = new MemoryStream(image);
        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.SupportedCommands, command =>
            command.Name == "program" && command.Source == "MappedHandlerTable");
        Assert.Contains(result.SupportedCommands, command =>
            command.Name == "configure" && command.Source == "MappedHandlerTable");
        Assert.Equal(QualcommVerificationStatus.NotChecked, result.DeviceUsabilityStatus);
    }

    [Fact]
    public void Analyze_LargeDeclaredLength_ReadsOnlyBoundedImageRanges()
    {
        var fixture = SignedImage.Create();
        using var stream = new CountingLargeStream(fixture.Elf);
        stream.Position = 11;

        var result = QualcommImageAnalyzer.Analyze(stream, fixture.Trust(), TestContext.Current.CancellationToken);

        Assert.Equal(QualcommVerificationStatus.Valid, result.HashStatus);
        Assert.InRange(stream.BytesRead, 1, 200_000);
        Assert.Equal(11, stream.Position);
    }

    [Fact]
    public void Analyze_UnsignedElf_DoesNotInferQualcommSignature()
    {
        var image = SignedImage.Create().Elf;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(64 + 56 + 4), 0x00000005);
        using var stream = new MemoryStream(image);

        var result = QualcommImageAnalyzer.Analyze(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QualcommImageFormat.Elf, result.Format);
        Assert.Null(result.Mbn);
        Assert.Equal(QualcommVerificationStatus.NotPresent, result.HashStatus);
        Assert.Equal(QualcommVerificationStatus.NotPresent, result.SignatureStatus);
    }

    private sealed class SignedImage
    {
        private readonly RSA _key;
        private readonly byte[] _root;
        private readonly byte[] _certificates;

        private SignedImage(RSA key, byte[] root, byte[] certificates, byte[] mbn, byte[] elf)
        {
            _key = key;
            _root = root;
            _certificates = certificates;
            Mbn = mbn;
            Elf = elf;
        }

        public byte[] Mbn { get; }
        public byte[] Elf { get; }

        public SignedImage WithHeaderVersion(uint version, uint? rootSlot = null,
            bool metadataHash = false, bool corruptRootDigest = false)
        {
            var sha384 = version is 6 or 7;
            var headerLength = version == 6 ? 48 : 40;
            var common = version == 7 ? 24 : 0;
            var oemMetadata = rootSlot.HasValue || metadataHash ? version == 7 ? 224 : 120 : 0;
            var hashLength = sha384 ? 96 : 64;
            var mbn = new byte[headerLength + common + oemMetadata + hashLength + 256 + _certificates.Length];
            Write(mbn, 0, version == 7 ? 0UL : 5UL);
            Write(mbn, 4, version);
            if (version == 7)
            {
                Write(mbn, 8, 24);
                Write(mbn, 16, (uint)oemMetadata);
                Write(mbn, 20, (uint)hashLength);
                Write(mbn, 32, 256);
                Write(mbn, 36, (uint)_certificates.Length);
                Write(mbn, 48, 3);
                Write(mbn, 56, 3);
            }
            else
            {
                Write(mbn, 16, (uint)(mbn.Length - headerLength));
                Write(mbn, 20, (uint)hashLength);
                Write(mbn, 28, 256);
                Write(mbn, 36, (uint)_certificates.Length);
                if (version == 6)
                {
                    Write(mbn, 44, (uint)oemMetadata);
                }
            }

            var metadataOffset = headerLength + common;
            if (rootSlot.HasValue)
            {
                Write(mbn, metadataOffset + 12 + (version == 6 ? 100 : 0), rootSlot.Value);
            }

            if (metadataHash)
            {
                Write(mbn, metadataOffset + 152, 2);
                SHA256.HashData(_root).CopyTo(mbn.AsSpan(metadataOffset + 156));
                if (corruptRootDigest)
                {
                    mbn[metadataOffset + 156] ^= 1;
                }
            }

            var table = headerLength + common + oemMetadata;
            (sha384 ? SHA384.HashData(Elf.AsSpan(0x200, 32)) : SHA256.HashData(Elf.AsSpan(0x200, 32)))
                .CopyTo(mbn.AsSpan(table));
            _key.SignData(mbn.AsSpan(0, table + hashLength),
                sha384 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CopyTo(mbn.AsSpan(table + hashLength));
            _certificates.CopyTo(mbn.AsSpan(table + hashLength + 256));
            var elf = new byte[0x400 + mbn.Length];
            Elf.AsSpan(0, 0x400).CopyTo(elf);
            Write(elf, 152, (ulong)mbn.Length, 8);
            Write(elf, 160, (ulong)mbn.Length, 8);
            mbn.CopyTo(elf.AsSpan(0x400));
            return new(_key, _root, _certificates, mbn, elf);
        }

        public QualcommAnalysisOptions Trust() => new()
        {
            TrustedRootCertificateSha256 = [Convert.ToHexString(SHA256.HashData(_root))]
        };

        public QualcommAnalysisOptions TrustSha384() => new()
        {
            TrustedRootCertificateSha384 = [Convert.ToHexString(SHA384.HashData(_root)).ToLowerInvariant()]
        };

        public byte[] CreateSbl()
        {
            var image = new byte[80 + 16 + 256 + _certificates.Length];
            Write(image, 0, 0x844bdcd1);
            Write(image, 4, 0x73d71034);
            Write(image, 20, 80);
            Write(image, 28, (uint)(16 + 256 + _certificates.Length));
            Write(image, 32, 16);
            Write(image, 40, 256);
            Write(image, 48, (uint)_certificates.Length);
            image.AsSpan(80, 16).Fill(0x42);
            var signature = _key.SignData(image.AsSpan(0, 96), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            signature.CopyTo(image.AsSpan(96));
            _certificates.CopyTo(image.AsSpan(352));
            return image;
        }

        public static SignedImage Create()
        {
            using var rootKey = RSA.Create(2048);
            var key = RSA.Create(2048);
            var rootRequest = new CertificateRequest("CN=Offline Root", rootKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            var now = DateTimeOffset.UtcNow;
            using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(7));
            var leafRequest = new CertificateRequest("CN=Offline Programmer", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var leaf = leafRequest.Create(root, now.AddHours(-12),
                now.AddDays(6), RandomNumberGenerator.GetBytes(16));
            var certs = new byte[leaf.RawData.Length + root.RawData.Length];
            leaf.RawData.CopyTo(certs, 0);
            root.RawData.CopyTo(certs, leaf.RawData.Length);

            var mbn = new byte[40 + 64 + 256 + certs.Length];
            Write(mbn, 0, 5);
            Write(mbn, 4, 5);
            Write(mbn, 16, (uint)(mbn.Length - 40));
            Write(mbn, 20, 64);
            Write(mbn, 28, 256);
            Write(mbn, 36, (uint)certs.Length);
            var elf = new byte[0x400 + mbn.Length];
            "\u007fELF"u8.CopyTo(elf);
            elf[4] = 2;
            elf[5] = elf[6] = 1;
            Write(elf, 16, 2, 2);
            Write(elf, 18, 183, 2);
            Write(elf, 20, 1);
            Write(elf, 32, 64, 8);
            Write(elf, 52, 64, 2);
            Write(elf, 54, 56, 2);
            Write(elf, 56, 2, 2);
            Write(elf, 64, 1);
            Write(elf, 68, 5);
            Write(elf, 72, 0x200, 8);
            Write(elf, 80, 0x2000, 8);
            Write(elf, 96, 32, 8);
            Write(elf, 104, 32, 8);
            Write(elf, 112, 1, 8);
            Write(elf, 120, 1);
            Write(elf, 124, 0x02200004);
            Write(elf, 128, 0x400, 8);
            Write(elf, 136, 0x4000, 8);
            Write(elf, 152, (ulong)mbn.Length, 8);
            Write(elf, 160, (ulong)mbn.Length, 8);
            Write(elf, 168, 1, 8);
            elf.AsSpan(0x200, 32).Fill(0x33);
            SHA256.HashData(elf.AsSpan(0x200, 32)).CopyTo(mbn.AsSpan(40));
            var signature = key.SignData(mbn.AsSpan(0, 104), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            signature.CopyTo(mbn.AsSpan(104));
            certs.CopyTo(mbn.AsSpan(360));
            mbn.CopyTo(elf.AsSpan(0x400));
            return new(key, root.RawData, certs, mbn, elf);
        }

        private static void Write(byte[] data, int offset, ulong value, int size = 4)
        {
            for (var i = 0; i < size; i++)
            {
                data[offset + i] = (byte)(value >> (8 * i));
            }
        }
    }

    private sealed class CountingLargeStream(byte[] data) : MemoryStream(data)
    {
        public long BytesRead { get; private set; }
        public override long Length => 8L * 1024 * 1024 * 1024;
        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer);
            BytesRead += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
    }
}
