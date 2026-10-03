using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk.Security;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Security;

public sealed class MtkSecCfgLayoutTests
{
    private const string ClosedHeaderHex = "4D4D4D4D040000003C00000001000000000000000000000045454545";
    private const string ClosedHeaderSha256Hex = "7EC2E74193DBE969BD1E899C0E5A9D9BDEF43DA366AA7DAB733CC1D111455422";
    private const string ClosedStoredHashHex = "BE83A6C7969908D7A61D11E93A3984A17A970C722F6318C3371DA3A52F038F44";
    private const string OpenHeaderHex = "4D4D4D4D040000003C00000003000000010000000000000045454545";
    private const string OpenStoredHashHex = "E0FAB6F6572E45F101F63D837AB70CBD91BF782B4BC6D4DAF5A2E2BD431FD904";
    private const string RuntimeOnHeaderHex = "4D4D4D4D040000003C00000003000000010000000100000045454545";
    private const string RuntimeOnHashHex = "307CB8E4A904F9A817653B8D7A948D2F32E83D3BE7330D7ED5CD8B23AABD2A99";
    private const string Version3BannerHex = "414E445F5345434346475F7600000000";
    private const string SizeConflictHeaderHex = "4D4D4D4D040000004000000001000000000000000000000045454545";
    private const string SizeConflictHashHex = "FB7F90FDB6158ACF56D2199EA61076EE50B66BAFA071D0828A1AE5AC292C3C00";

    [Fact]
    public void TryReadVersion4_SoftwareProtectedImage_DecodesEveryDocumentedDword()
    {
        var image = BuildVersion4Image(ClosedHeaderHex, ClosedStoredHashHex);

        Assert.True(MtkSecCfg.TryReadVersion4(image, out var report));
        Assert.NotNull(report);
        Assert.Equal(ClosedHeaderHex, report.HeaderHex);
        Assert.Equal(MtkSecCfg.Magic, report.Magic);
        Assert.Equal(4u, report.Version);
        Assert.Equal(MtkSecCfg.Version4AgreedSize, report.Size);
        Assert.Equal(1u, report.LockStateRaw);
        Assert.Equal(0u, report.CriticalLockStateRaw);
        Assert.Equal(0u, report.RuntimeRaw);
        Assert.Equal(MtkSecCfg.EndFlag, report.EndFlag);
        Assert.Equal(ClosedStoredHashHex, report.StoredHashHex);
        Assert.Equal(ClosedHeaderSha256Hex, report.ComputedHashHex);
        Assert.Equal(MtkSecCfgHashKind.Software, report.HashKind);
        Assert.Equal(MtkSecCfgLockState.Default, report.LockState);
        Assert.Null(report.CriticalLockState);
        Assert.Equal(MtkSecCfgRuntimeState.Off, report.Runtime);
        Assert.True(report.IsClosed);
        Assert.False(report.IsOpen);
    }

    [Fact]
    public void TryReadVersion4_UnmatchedStoredHash_ReportsUnresolvedInsteadOfGuessingHardware()
    {
        var image = BuildVersion4Image(ClosedHeaderHex, new string('0', 64));

        Assert.True(MtkSecCfg.TryReadVersion4(image, out var report));
        Assert.NotNull(report);
        Assert.Equal(MtkSecCfgHashKind.Unresolved, report.HashKind);
        Assert.NotEqual(report.StoredHashHex, report.ComputedHashHex);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("bad-magic")]
    [InlineData("bad-end-flag")]
    [InlineData("undersized-declared")]
    [InlineData("declared-beyond-image")]
    public void TryReadVersion4_CorruptHeader_FailsWithoutThrowing(string corruption)
    {
        var image = BuildVersion4Image(ClosedHeaderHex, ClosedStoredHashHex);
        switch (corruption)
        {
            case "short":
                image = image[..(MtkSecCfg.Version4HeaderLength - 4)];
                break;
            case "bad-magic":
                image[0] = 0x4E;
                break;
            case "bad-end-flag":
                image[24] = 0x44;
                break;
            case "undersized-declared":
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 0x20);
                break;
            case "declared-beyond-image":
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)image.Length + 4);
                break;
        }

        Assert.False(MtkSecCfg.TryReadVersion4(image, out var report));
        Assert.Null(report);
    }

    [Fact]
    public void CreateVersion4_Unlock_WritesTheDocumentedDwordsAndSoftwareHash()
    {
        Assert.True(MtkSecCfg.TryReadVersion4(
            BuildVersion4Image(ClosedHeaderHex, ClosedStoredHashHex), out var report));
        Assert.NotNull(report);

        var image = MtkSecCfg.CreateVersion4(report, MtkSecCfgLockIntent.Unlock);

        Assert.Equal(0x200, image.Length);
        Assert.Equal(0, image.Length % MtkSecCfg.ImageAlignment);
        Assert.Equal([.. Convert.FromHexString(OpenHeaderHex), .. Convert.FromHexString(OpenStoredHashHex)],
            image[..0x3C]);
        Assert.Equal(new byte[0x200 - 0x3C], image[0x3C..]);
    }

    [Fact]
    public void CreateVersion4_Lock_WritesClosedDwordsAndPreservesRuntimeAndVersion()
    {
        var image = BuildVersion4Image(RuntimeOnHeaderHex, RuntimeOnHashHex);
        Assert.True(MtkSecCfg.TryReadVersion4(image, out var report));
        Assert.NotNull(report);
        Assert.Equal(MtkSecCfgHashKind.Software, report.HashKind);
        Assert.Equal(MtkSecCfgRuntimeState.On, report.Runtime);

        var written = MtkSecCfg.CreateVersion4(report, MtkSecCfgLockIntent.Lock);

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(16)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(20)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(4)));
        Assert.Equal(MtkSecCfg.EndFlag, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(24)));
        Assert.Equal(0x200, written.Length);
    }

    [Theory]
    [InlineData(MtkSecCfgLockIntent.Lock, "already closed")]
    [InlineData(MtkSecCfgLockIntent.Unlock, "already open")]
    public void CreateVersion4_StateAlreadyApplied_RefusesWithoutProducingBytes(MtkSecCfgLockIntent intent,
        string expectedReason)
    {
        var headerHex = intent == MtkSecCfgLockIntent.Lock ? ClosedHeaderHex : OpenHeaderHex;
        var storedHash = intent == MtkSecCfgLockIntent.Lock ? ClosedStoredHashHex : OpenStoredHashHex;
        Assert.True(MtkSecCfg.TryReadVersion4(BuildVersion4Image(headerHex, storedHash), out var report));
        Assert.NotNull(report);

        var exception = Assert.Throws<InvalidOperationException>(() => MtkSecCfg.CreateVersion4(report, intent));

        Assert.Contains(expectedReason, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateVersion4_HardwareProtectedHash_RefusesInsteadOfApproximating()
    {
        Assert.True(MtkSecCfg.TryReadVersion4(
            BuildVersion4Image(ClosedHeaderHex, new string('0', 64)), out var report));
        Assert.NotNull(report);

        Assert.Throws<NotSupportedException>(() => MtkSecCfg.CreateVersion4(report, MtkSecCfgLockIntent.Unlock));
    }

    [Fact]
    public void CreateVersion4_DeclaredSizeContradictingWriterLayout_Refuses()
    {
        var image = new byte[0x200];
        Convert.FromHexString(SizeConflictHeaderHex).CopyTo(image, 0);
        Convert.FromHexString(SizeConflictHashHex).CopyTo(image, 0x20);

        Assert.True(MtkSecCfg.TryReadVersion4(image, out var report));
        Assert.NotNull(report);
        Assert.Equal(0x40u, report.Size);
        Assert.Equal(MtkSecCfgHashKind.Software, report.HashKind);

        var exception = Assert.Throws<NotSupportedException>(
            () => MtkSecCfg.CreateVersion4(report, MtkSecCfgLockIntent.Unlock));
        Assert.Contains("0x40", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateVersion4_NullReport_ThrowsBeforeAnyLayoutWork()
    {
        Assert.Throws<ArgumentNullException>(() => MtkSecCfg.CreateVersion4(null!, MtkSecCfgLockIntent.Unlock));
    }

    [Fact]
    public void TryReadVersion3_SizedBodyAtDocumentedOffsets_DecodesEveryHeaderField()
    {
        var image = BuildVersion3Image(0x130, bodyLength: 0x100);

        Assert.True(MtkSecCfg.TryReadVersion3(image, out var report));
        Assert.NotNull(report);
        Assert.Equal(Version3BannerHex, report.BannerHex);
        Assert.Equal(MtkSecCfg.Magic, report.Magic);
        Assert.Equal(3u, report.Version);
        Assert.Equal(0x130u, report.Size);
        Assert.Equal(0u, report.EncryptionOffset);
        Assert.Equal(MtkSecCfg.Version3ClosedEncryptionLength, report.EncryptionLength);
        Assert.Equal((byte)2, report.SoftwareLockTry);
        Assert.Equal((byte)1, report.SoftwareLockDone);
        Assert.Equal((ushort)0x200, report.PageSize);
        Assert.Equal(7u, report.PageCount);
        Assert.Equal(MtkSecCfg.EndFlag, report.EndFlag);
        Assert.Equal(0x100, report.BodyLength);
        Assert.Equal("40AFF2E9D2D8922E47AFD4648E6967497158785FBD1DA870E7110266BF944880", report.BodySha256Hex);
        Assert.Equal(MtkSecCfgLockIntent.Lock, report.DocumentedState);
    }

    [Fact]
    public void TryReadVersion3_OpenEncryptionLengthWord_ClassifiesAsUnlock()
    {
        var image = BuildVersion3Image(0x130, bodyLength: 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), MtkSecCfg.Version3OpenEncryptionLength);

        Assert.True(MtkSecCfg.TryReadVersion3(image, out var report));
        Assert.NotNull(report);
        Assert.Equal(MtkSecCfgLockIntent.Unlock, report.DocumentedState);
    }

    [Theory]
    [InlineData("no-banner")]
    [InlineData("truncated-header")]
    [InlineData("size-inside-header")]
    [InlineData("size-beyond-image")]
    [InlineData("bad-magic")]
    [InlineData("bad-end-flag")]
    public void TryReadVersion3_CorruptHeader_FailsWithoutThrowing(string corruption)
    {
        var image = BuildVersion3Image(0x130, bodyLength: 0x100);
        switch (corruption)
        {
            case "no-banner":
                image[0] = 0x42;
                break;
            case "truncated-header":
                image = image[..(MtkSecCfg.Version3HeaderLength + 3)];
                break;
            case "size-inside-header":
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x18), 0x20);
                break;
            case "size-beyond-image":
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x18), (uint)image.Length + 4);
                break;
            case "bad-magic":
                image[0x10] = 0x4E;
                break;
            case "bad-end-flag":
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x12C), 0x44444444);
                break;
        }

        Assert.False(MtkSecCfg.TryReadVersion3(image, out var report));
        Assert.Null(report);
    }

    [Theory]
    [InlineData("version3", MtkSecCfgFormat.Version3)]
    [InlineData("version4", MtkSecCfgFormat.Version4)]
    [InlineData("unknown", -1)]
    [InlineData("empty", -1)]
    public void DetectFormat_HeaderBytesOnly_SelectsDocumentedLayout(string shape, int expected)
    {
        var image = shape switch
        {
            "version3" => BuildVersion3Image(0x130, bodyLength: 0x100),
            "version4" => BuildVersion4Image(ClosedHeaderHex, ClosedStoredHashHex),
            "unknown" => new byte[0x200],
            _ => []
        };

        var format = MtkSecCfg.DetectFormat(image);

        Assert.Equal(expected == -1 ? null : (MtkSecCfgFormat)expected, format);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1FF)]
    [InlineData(0x201)]
    public void ValidateWriteCandidate_UnalignedImage_Rejects(int length)
    {
        Assert.Throws<ArgumentException>(() => MtkSecCfg.ValidateWriteCandidate(new byte[length]));
    }

    [Fact]
    public void ValidateWriteCandidate_DocumentedHeaders_ReturnLayoutAndUnknownHeader_Rejects()
    {
        Assert.Equal(MtkSecCfgFormat.Version4,
            MtkSecCfg.ValidateWriteCandidate(BuildVersion4Image(ClosedHeaderHex, ClosedStoredHashHex)));
        Assert.Equal(MtkSecCfgFormat.Version3,
            MtkSecCfg.ValidateWriteCandidate(BuildVersion3Image(0x130, bodyLength: 0x100)));

        var header = new byte[0x200];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4D4D4D4E);
        Assert.Throws<ArgumentException>(() => MtkSecCfg.ValidateWriteCandidate(header));
    }

    [Fact]
    public void SecCfgWriteResult_ReportsSubmittedDigestAndLocation()
    {
        var result = new MtkSecCfgWriteResult("seccfg", 4, 20480, 2048, MtkSecCfgFormat.Version4, ClosedHeaderSha256Hex);

        Assert.Equal("seccfg", result.PartitionName);
        Assert.Equal(4, result.PartitionIndex);
        Assert.Equal(20480, result.ByteOffset);
        Assert.Equal(2048, result.ByteLength);
        Assert.Equal(MtkSecCfgFormat.Version4, result.Format);
        Assert.Equal(ClosedHeaderSha256Hex, result.ImageSha256Hex);
    }

    private static byte[] BuildVersion4Image(string headerHex, string storedHashHex)
    {
        var image = new byte[0x200];
        Convert.FromHexString(headerHex).CopyTo(image, 0);
        Convert.FromHexString(storedHashHex).CopyTo(image, 0x1C);
        return image;
    }

    private static byte[] BuildVersion3Image(uint declaredSize, int bodyLength)
    {
        var image = new byte[0x200];
        "AND_SECCFG_v\0\0\0\0"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x10), MtkSecCfg.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x18), declaredSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x1C), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), MtkSecCfg.Version3ClosedEncryptionLength);
        image[0x24] = 2;
        image[0x25] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x26), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x28), 7);
        for (var index = 0; index < bodyLength; index++)
        {
            image[MtkSecCfg.Version3HeaderLength + index] = (byte)index;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)declaredSize - 4), MtkSecCfg.EndFlag);
        return image;
    }
}

public sealed class MtkSecCfgStatusVectorTests
{
    [Theory]
    [InlineData(0x00000000u, "OK")]
    [InlineData(0xc0010003u, "Unsupported command")]
    [InlineData(0xc002002eu, "SECCFG not found")]
    [InlineData(0xc002002fu, "SECCFG magic incorrect")]
    [InlineData(0xc0020030u, "SECCFG invalid")]
    [InlineData(0xc0020044u, "Comm Key is not set")]
    [InlineData(0xc0020051u, "Otp table not initialized")]
    [InlineData(0xc002005au, "Security config is formatted")]
    [InlineData(0xc002005eu, "Lockstate inconsistent")]
    public void TryDescribe_CataloguedStatusCode_ReturnsReferenceName(uint status, string expected)
    {
        Assert.Equal(expected, MtkSecStatusCode.TryDescribe(status));
    }

    [Theory]
    [InlineData(0xc0010001u)]
    [InlineData(0xc0020021u)]
    [InlineData(0xc002005fu)]
    [InlineData(0xffffffffu)]
    public void TryDescribe_UncataloguedStatusCode_StaysNullAndDescribeKeepsRawValue(uint status)
    {
        Assert.Null(MtkSecStatusCode.TryDescribe(status));
        Assert.Equal($"0x{status:X8}", MtkSecStatusCode.Describe(status));
        Assert.DoesNotContain("(", MtkSecStatusCode.Describe(status), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_CataloguedStatusCode_PairsNameWithRawValue()
    {
        Assert.Equal("0xC0020030 (SECCFG invalid)", MtkSecStatusCode.Describe(0xc0020030));
        Assert.Equal("0x00000000 (OK)", MtkSecStatusCode.Describe(MtkSecStatusCode.Ok));
    }

    [Theory]
    [InlineData((ushort)0, "OK")]
    [InlineData((ushort)1, "General failure")]
    [InlineData((ushort)2, "Authentication failure")]
    [InlineData((ushort)3, "Counter failure")]
    [InlineData((ushort)4, "Address failure")]
    [InlineData((ushort)5, "Write failure")]
    [InlineData((ushort)6, "Read failure")]
    [InlineData((ushort)7, "Authentication key not yet programmed")]
    public void ReplayProtectedReply_TableMatchesReferenceWords(ushort status, string expected)
    {
        Assert.Equal(expected, MtkSecRpmbStatusCode.TryDescribe(status));
        Assert.Equal($"0x{status:X4} ({expected})", MtkSecRpmbStatusCode.Describe(status));
    }

    [Fact]
    public void ReplayProtectedReply_UnknownWord_StaysRaw()
    {
        Assert.Null(MtkSecRpmbStatusCode.TryDescribe(8));
        Assert.Equal("0x0008", MtkSecRpmbStatusCode.Describe(8));
    }
}