using System.Text;
using UotanStudio.Firmware.Cpio;
using UotanStudio.Firmware.Cpio.Model;
using UotanStudio.Firmware.Cpio.Writing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// CPIO (newc) pack → unpack round-trip tests.
/// <para>CPIO（newc）打包 → 解包往返测试。</para>
/// </summary>
public class CpioRoundTripTests
{
    [Fact]
    public void PackUnpackNewc_RoundTripsEntriesAndRepacksByteIdentically()
    {
        var entries = new[]
        {
            new ArchiveEntry
            {
                Name = "dir/",
                Data = [],
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Directory,
                    Mode = 0x41ED, // S_IFDIR | 0755
                    UserId = 0,
                    GroupId = 0,
                    LinkCount = 2,
                    ModificationTimeUnixSeconds = 1_700_000_000,
                    UnixPermissions = 0x1ED,
                },
            },
            new ArchiveEntry
            {
                Name = "dir/hello.txt",
                Data = Encoding.UTF8.GetBytes("hello cpio\n"),
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Regular,
                    Mode = 0x81A4, // S_IFREG | 0644
                    UserId = 1000,
                    GroupId = 1000,
                    LinkCount = 1,
                    ModificationTimeUnixSeconds = 1_700_000_001,
                    UnixPermissions = 0x1A4,
                },
            },
            new ArchiveEntry
            {
                Name = "dir/blob.bin",
                Data = [0x00, 0x01, 0xFE, 0xFF, 0x80, 0x7F],
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Regular,
                    Mode = 0x81A4,
                    UserId = 1000,
                    GroupId = 1000,
                    LinkCount = 1,
                    ModificationTimeUnixSeconds = 1_700_000_002,
                    UnixPermissions = 0x1A4,
                },
            },
        };

        var archive = new CpioArchive();

        byte[] packed;
        using (var ms = new MemoryStream())
        {
            archive.SaveAll(ms, entries, ArchiveFormat.NewAscii, new WriteOptions { IncludeTrailer = true });
            packed = ms.ToArray();
        }

        // newc magic.
        Assert.StartsWith("070701", Encoding.ASCII.GetString(packed, 0, 6));

        IReadOnlyList<ArchiveEntry> unpacked;
        using (var ms = new MemoryStream(packed, writable: false))
        {
            unpacked = archive.ReadAll(ms, ArchiveFormat.NewAscii);
        }

        Assert.Equal(entries.Length, unpacked.Count);

        foreach (var original in entries)
        {
            var roundTripped = unpacked.Single(e => e.Name == original.Name);
            Assert.Equal(original.Data, roundTripped.Data);
            Assert.Equal(original.Metadata.FileType, roundTripped.Metadata.FileType);
            Assert.Equal(original.Metadata.Mode, roundTripped.Metadata.Mode);
            Assert.Equal(original.Metadata.UserId, roundTripped.Metadata.UserId);
            Assert.Equal(original.Metadata.GroupId, roundTripped.Metadata.GroupId);
            Assert.Equal(original.Metadata.LinkCount, roundTripped.Metadata.LinkCount);
            Assert.Equal(original.Metadata.ModificationTimeUnixSeconds, roundTripped.Metadata.ModificationTimeUnixSeconds);
            Assert.Equal(original.Metadata.DeviceMajor, roundTripped.Metadata.DeviceMajor);
            Assert.Equal(original.Metadata.DeviceMinor, roundTripped.Metadata.DeviceMinor);
        }

        // Re-pack the unpacked entries with the same options: the newc output must be byte-identical.
        byte[] repacked;
        using (var ms = new MemoryStream())
        {
            archive.SaveAll(ms, unpacked, ArchiveFormat.NewAscii, new WriteOptions { IncludeTrailer = true });
            repacked = ms.ToArray();
        }

        Assert.Equal(packed, repacked);
    }

    [Fact]
    public void SaveAll_EmptyEntries_WritesValidNewcTrailer()
    {
        var archive = new CpioArchive();
        using var ms = new MemoryStream();
        archive.SaveAll(ms, Array.Empty<ArchiveEntry>(), ArchiveFormat.NewAscii, new WriteOptions { IncludeTrailer = true });
        var bytes = ms.ToArray();

        Assert.NotEmpty(bytes);
        Assert.StartsWith("070701", Encoding.ASCII.GetString(bytes, 0, 6));

        // Only the TRAILER!!! entry is present; reading yields no entries.
        using var readStream = new MemoryStream(bytes, writable: false);
        Assert.Empty(archive.ReadAll(readStream, ArchiveFormat.NewAscii));
    }
}
