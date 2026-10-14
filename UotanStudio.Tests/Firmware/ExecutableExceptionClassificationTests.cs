using System.Buffers.Binary;
using UotanStudio.Firmware.Executable;
using UotanStudio.Firmware.Executable.Qualcomm;
using UotanStudio.Hash.Digests;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ExecutableExceptionClassificationTests
{
    private static void Word(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);

    [Fact]
    public void MbnLayout_Read_HeaderBelowMinimumLength_ReportsCorruption()
    {
        using var stream = new MemoryStream(new byte[20]);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => QualcommMbnLayout.Read(stream, 0, 20, TestContext.Current.CancellationToken));
        Assert.Equal("Truncated Qualcomm MBN header.", error.Message);
    }

    [Fact]
    public void MbnLayout_Read_UnknownHeaderVersion_ReportsUnsupportedFeature()
    {
        var data = new byte[40];
        Word(data, 4, 9);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableUnsupportedFeatureException>(
            () => QualcommMbnLayout.Read(stream, 0, data.Length, TestContext.Current.CancellationToken));
        Assert.Equal("Unsupported Qualcomm MBN header version.", error.Message);
    }

    [Fact]
    public void MbnLayout_Read_WideHeaderVersionBelowFortyEightBytes_ReportsCorruption()
    {
        var data = new byte[44];
        Word(data, 4, 6);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => QualcommMbnLayout.Read(stream, 0, data.Length, TestContext.Current.CancellationToken));
        Assert.Equal("Truncated Qualcomm MBN header.", error.Message);
    }

    [Fact]
    public void MbnLayout_Read_ShortCommonMetadataLength_ReportsCorruption()
    {
        var data = new byte[40];
        Word(data, 4, 7);
        Word(data, 8, 23);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => QualcommMbnLayout.Read(stream, 0, data.Length, TestContext.Current.CancellationToken));
        Assert.Equal("Invalid MBN v7 common metadata length.", error.Message);
    }

    [Fact]
    public void MbnLayout_Read_EmptyHashTable_ReportsCorruption()
    {
        var data = new byte[40];
        Word(data, 4, 5);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => QualcommMbnLayout.Read(stream, 0, data.Length, TestContext.Current.CancellationToken));
        Assert.Equal("Qualcomm MBN hash table is empty.", error.Message);
    }

    [Fact]
    public void MbnLayout_Read_AuthenticationRangesExceedDeclaredLength_ReportsCorruption()
    {
        var data = new byte[40];
        Word(data, 4, 5);
        Word(data, 20, 64);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => QualcommMbnLayout.Read(stream, 0, data.Length, TestContext.Current.CancellationToken));
        Assert.Equal("Qualcomm MBN length or authentication ranges are invalid.", error.Message);
    }

    [Fact]
    public void MbnLayout_Read_SevenByteRootSlotWordAboveSupportedBound_ReportsUnsupportedFeature()
    {
        var data = new byte[144];
        Word(data, 4, 7);
        Word(data, 8, 24);
        Word(data, 12, 16);
        Word(data, 20, 64);
        Word(data, 76, 64);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableUnsupportedFeatureException>(
            () => QualcommMbnLayout.Read(stream, 0, data.Length, TestContext.Current.CancellationToken));
        Assert.Equal("MBN root-certificate slot exceeds the supported limit.", error.Message);
    }

    [Fact]
    public void MbnLayout_ReadAt_RangeBeyondFile_ReportsCorruption()
    {
        using var stream = new MemoryStream(new byte[16]);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => QualcommMbnLayout.ReadAt(stream, 12, new byte[8], TestContext.Current.CancellationToken));
        Assert.Equal("Qualcomm data range exceeds the file.", error.Message);
        Assert.Throws<ExecutableCorruptedException>(
            () => QualcommMbnLayout.ReadAt(stream, -1, new byte[4], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MelfComponents_Scan_ExcessiveComponentCount_ReportsCorruption()
    {
        var data = new byte[64 * 257];
        for (var index = 0; index < 257; index++)
        {
            "\u007fELF"u8.CopyTo(data.AsSpan(index * 64));
        }
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => QualcommMelfComponents.Scan(stream, TestContext.Current.CancellationToken));
        Assert.Equal("Qualcomm multi-ELF component count exceeds the supported limit.", error.Message);
    }

    [Fact]
    public void AppendRange_SignedRangeBeyondFile_ReportsCorruption()
    {
        using var stream = new MemoryStream(new byte[32]);
        using var hash = Sha256.CreateAccumulator();

        Assert.Throws<ExecutableCorruptedException>(() => QualcommImageAnalyzer.AppendRange(
            hash, stream, 0, 33, TestContext.Current.CancellationToken));
        Assert.Throws<ExecutableCorruptedException>(() => QualcommImageAnalyzer.AppendRange(
            hash, stream, -1, 8, TestContext.Current.CancellationToken));
    }
}