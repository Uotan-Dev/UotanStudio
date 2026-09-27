using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive;
using UotanStudio.Firmware.BoundedContexts.Archive.Models;
using UotanStudio.Firmware.BoundedContexts.Archive.Writing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Write-side parity tests for every CPIO dialect the reader parses: newc, crc, odc, HP odc, binary and HP binary.
/// <para>为读取器已解析的全部 CPIO 方言（newc、crc、odc、HP odc、binary 与 HP binary）提供写侧对等测试。</para>
/// </summary>
public sealed partial class CpioVariantRoundTripTests
{
    private static readonly ArchiveFormat[] AllDialects =
    [
        ArchiveFormat.NewAscii,
        ArchiveFormat.CrcAscii,
        ArchiveFormat.OldAscii,
        ArchiveFormat.HpOldAscii,
        ArchiveFormat.Binary,
        ArchiveFormat.HpBinary,
    ];

    [Theory]
    [MemberData(nameof(AllDialectsData))]
    public void SaveAll_DialectWithFileDirectoryAndSymlink_RoundTripsStructureAndRepacksByteIdentically(ArchiveFormat format)
    {
        var payload = BuildBytes(4114, 31);
        var entries = BuildMixedEntries(payload);
        var archive = new CpioArchive();

        var packed = Write(archive, entries, format);
        if (format is ArchiveFormat.Binary or ArchiveFormat.HpBinary)
        {
            Assert.Equal([0x71, 0xC7], packed[..2]);
        }
        else
        {
            Assert.Equal(MagicText(format), Encoding.ASCII.GetString(packed, 0, 6));
        }

        var unpacked = Read(archive, packed, format);
        Assert.Equal(3, unpacked.Count);

        var file = unpacked.Single(entry => entry.Name == "payload");
        Assert.Equal(payload, file.Data);
        Assert.Equal(CpioFileType.Regular, file.Metadata.FileType);
        Assert.Equal(0x81A4U, file.Metadata.Mode);
        Assert.Equal(1000U, file.Metadata.UserId);
        Assert.Equal(1000U, file.Metadata.GroupId);
        Assert.Equal(17UL, file.Metadata.Inode);
        Assert.Equal(1_700_000_000L, file.Metadata.ModificationTimeUnixSeconds);
        Assert.Equal(3U, file.Metadata.DeviceMajor);

        var directory = unpacked.Single(entry => entry.Name == "dir");
        Assert.Equal(CpioFileType.Directory, directory.Metadata.FileType);
        Assert.Empty(directory.Data);

        var symlink = unpacked.Single(entry => entry.Name == "link");
        Assert.Equal(CpioFileType.SymbolicLink, symlink.Metadata.FileType);
        Assert.Equal("dir", symlink.LinkName);

        Assert.Equal(packed, Write(archive, unpacked, format));
    }

    [Theory]
    [InlineData(ArchiveFormat.NewAscii)]
    [InlineData(ArchiveFormat.CrcAscii)]
    [InlineData(ArchiveFormat.OldAscii)]
    [InlineData(ArchiveFormat.Binary)]
    public void ReadEntries_WithoutHint_AutoDetectsNonHpDialect(ArchiveFormat format)
    {
        var archive = new CpioArchive();
        var packed = Write(archive, BuildMixedEntries([1, 2, 3]), format);
        using var stream = new MemoryStream(packed, writable: false);
        Assert.Equal(format, archive.DetectFormat(stream));
        Assert.Equal(3, archive.ReadAll(stream).Count);
    }

    [Theory]
    [InlineData(ArchiveFormat.CrcAscii)]
    [InlineData(ArchiveFormat.OldAscii)]
    [InlineData(ArchiveFormat.Binary)]
    public void SaveAll_StreamedPayload_RoundTripsThroughNonMaterializingSource(ArchiveFormat format)
    {
        var payload = BuildBytes(2069, 17);
        var archive = new CpioArchive();
        var entries = new[]
        {
            new ArchiveEntry
            {
                Name = "streamed",
                Data = [],
                DataStream = new MemoryStream(payload),
                DeclaredDataLength = payload.Length,
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Regular,
                    Mode = 0x81A4,
                    Inode = 21,
                    LinkCount = 1,
                    ModificationTimeUnixSeconds = 1_700_000_050,
                },
            },
        };

        var packed = Write(archive, entries, format);
        var unpacked = Read(archive, packed, format);
        var streamed = unpacked.Single(entry => entry.Name == "streamed");
        Assert.Equal(payload, streamed.Data);
        Assert.Equal(21UL, streamed.Metadata.Inode);
    }

    [Theory]
    [InlineData(ArchiveFormat.HpOldAscii)]
    [InlineData(ArchiveFormat.HpBinary)]
    public void SaveAll_HpDialectWithSpecialDevices_PacksDeviceNumbersIntoSizeFieldAndRepacksByteIdentically(ArchiveFormat format)
    {
        var entries = new[]
        {
            new ArchiveEntry
            {
                Name = "console",
                Data = [],
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.CharacterDevice,
                    Mode = 0x21B6,
                    Inode = 5,
                    LinkCount = 1,
                    ModificationTimeUnixSeconds = 1_700_000_060,
                    RDeviceMajor = 5,
                    RDeviceMinor = 1,
                },
            },
            new ArchiveEntry
            {
                Name = "mmcblk0",
                Data = [],
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.BlockDevice,
                    Mode = 0x61B0,
                    Inode = 6,
                    LinkCount = 1,
                    ModificationTimeUnixSeconds = 1_700_000_061,
                    RDeviceMajor = 179,
                    RDeviceMinor = 0,
                },
            },
        };
        var archive = new CpioArchive();

        var packed = Write(archive, entries, format);
        var unpacked = Read(archive, packed, format);
        Assert.Equal(2, unpacked.Count);

        var console = unpacked.Single(entry => entry.Name == "console");
        Assert.Equal(CpioFileType.CharacterDevice, console.Metadata.FileType);
        Assert.Equal(5U, console.Metadata.RDeviceMajor);
        Assert.Equal(1U, console.Metadata.RDeviceMinor);
        Assert.Empty(console.Data);

        var mmc = unpacked.Single(entry => entry.Name == "mmcblk0");
        Assert.Equal(CpioFileType.BlockDevice, mmc.Metadata.FileType);
        Assert.Equal(179U, mmc.Metadata.RDeviceMajor);

        Assert.Equal(packed, Write(archive, unpacked, format));
    }

    [Theory]
    [InlineData(ArchiveFormat.HpOldAscii)]
    [InlineData(ArchiveFormat.HpBinary)]
    public void SaveAll_HpSpecialDeviceWithPayload_ThrowsInvalidData(ArchiveFormat format)
    {
        var archive = new CpioArchive();
        var entries = new[]
        {
            new ArchiveEntry
            {
                Name = "bad",
                Data = [1, 2, 3],
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.CharacterDevice,
                    Mode = 0x21B6,
                    LinkCount = 1,
                    RDeviceMajor = 1,
                    RDeviceMinor = 1,
                },
            },
        };

        Assert.Throws<InvalidDataException>(() => Write(archive, entries, format));
    }

    [Theory]
    [InlineData(ArchiveFormat.NewAscii)]
    [InlineData(ArchiveFormat.CrcAscii)]
    [InlineData(ArchiveFormat.OldAscii)]
    [InlineData(ArchiveFormat.HpOldAscii)]
    [InlineData(ArchiveFormat.Binary)]
    [InlineData(ArchiveFormat.HpBinary)]
    public void SaveAll_HardlinkedGroup_RoundTripsThroughDataCarrierFolding(ArchiveFormat format)
    {
        var payload = BuildBytes(64, 7);
        var entries = new[]
        {
            new ArchiveEntry
            {
                Name = "carrier",
                Data = payload,
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Regular,
                    Mode = 0x81A4,
                    Inode = 42,
                    LinkCount = 2,
                    ModificationTimeUnixSeconds = 1_700_000_070,
                },
            },
            new ArchiveEntry
            {
                Name = "twin",
                Data = payload,
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Regular,
                    Mode = 0x81A4,
                    Inode = 42,
                    LinkCount = 2,
                    ModificationTimeUnixSeconds = 1_700_000_070,
                },
            },
        };
        var archive = new CpioArchive();

        var packed = Write(archive, entries, format);
        var unpacked = Read(archive, packed, format);
        Assert.Equal(2, unpacked.Count);
        Assert.All(unpacked, entry => Assert.Equal(payload, entry.Data));
        if (format is ArchiveFormat.NewAscii or ArchiveFormat.CrcAscii)
        {
            Assert.Equal("carrier", unpacked.Single(entry => entry.Name == "twin").LinkName);
        }
    }

    public static TheoryData<ArchiveFormat> AllDialectsData => new(AllDialects);

    private static ArchiveEntry[] BuildMixedEntries(byte[] payload) =>
    [
        new ArchiveEntry
        {
            Name = "payload",
            Data = payload,
            Metadata = new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Regular,
                Mode = 0x81A4,
                UserId = 1000,
                GroupId = 1000,
                Inode = 17,
                LinkCount = 1,
                ModificationTimeUnixSeconds = 1_700_000_000,
                DeviceMajor = 3,
            },
        },
        new ArchiveEntry
        {
            Name = "dir",
            Data = [],
            Metadata = new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Directory,
                Mode = 0x41ED,
                Inode = 18,
                LinkCount = 2,
                ModificationTimeUnixSeconds = 1_700_000_001,
                DeviceMajor = 3,
            },
        },
        new ArchiveEntry
        {
            Name = "link",
            Data = [],
            LinkName = "dir",
            Metadata = new ArchiveEntryMetadata
            {
                FileType = CpioFileType.SymbolicLink,
                Mode = 0xA1FF,
                Inode = 19,
                LinkCount = 1,
                ModificationTimeUnixSeconds = 1_700_000_002,
                DeviceMajor = 3,
            },
        },
    ];

    private static byte[] Write(CpioArchive archive, IEnumerable<ArchiveEntry> entries, ArchiveFormat format)
    {
        using var stream = new MemoryStream();
        archive.SaveAll(stream, entries, format, new WriteOptions());
        return stream.ToArray();
    }

    private static IReadOnlyList<ArchiveEntry> Read(CpioArchive archive, byte[] packed, ArchiveFormat format)
    {
        using var stream = new MemoryStream(packed, writable: false);
        return archive.ReadAll(stream, format);
    }

    private static string MagicText(ArchiveFormat format) => format switch
    {
        ArchiveFormat.NewAscii => "070701",
        ArchiveFormat.CrcAscii => "070702",
        ArchiveFormat.OldAscii or ArchiveFormat.HpOldAscii => "070707",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static byte[] BuildBytes(int length, int factor)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * factor) & 0xFF);
        }
        return data;
    }
}