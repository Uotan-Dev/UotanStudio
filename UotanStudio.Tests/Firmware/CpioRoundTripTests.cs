using System.Text;
using UotanStudio.Firmware.Cpio;
using UotanStudio.Firmware.Cpio.Formats.Tar;
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

    [Fact]
    public void PackNewc_WithFileStreamData_RoundTrips()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cpio_stream_newc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var fileA = Path.Combine(tmpDir, "a.bin");
            var fileB = Path.Combine(tmpDir, "b.bin");
            var contentA = BuildBytes(4096, 3);
            var contentB = BuildBytes(4096, 11);
            File.WriteAllBytes(fileA, contentA);
            File.WriteAllBytes(fileB, contentB);

            var entries = new[]
            {
                BuildStreamedEntry("a.bin", fileA),
                BuildStreamedEntry("b.bin", fileB),
            };

            var archive = new CpioArchive();
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                archive.SaveAll(ms, entries, ArchiveFormat.NewAscii, new WriteOptions { IncludeTrailer = true });
                packed = ms.ToArray();
            }

            using var readStream = new MemoryStream(packed, writable: false);
            var unpacked = archive.ReadAll(readStream, ArchiveFormat.NewAscii);

            Assert.Equal(2, unpacked.Count);
            Assert.Equal(contentA, unpacked.Single(e => e.Name == "a.bin").Data);
            Assert.Equal(contentB, unpacked.Single(e => e.Name == "b.bin").Data);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void PackTar_WithFileStreamData_RoundTrips()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cpio_stream_tar_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var fileA = Path.Combine(tmpDir, "a.bin");
            var fileB = Path.Combine(tmpDir, "b.bin");
            var contentA = BuildBytes(8192, 5);
            var contentB = BuildBytes(2048, 9);
            File.WriteAllBytes(fileA, contentA);
            File.WriteAllBytes(fileB, contentB);

            var entries = new[]
            {
                BuildStreamedEntry("a.bin", fileA),
                BuildStreamedEntry("b.bin", fileB),
            };

            var archive = new CpioArchive();
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                archive.SaveAll(ms, entries, ArchiveFormat.Ustar, new WriteOptions { IncludeTrailer = true });
                packed = ms.ToArray();
            }

            using var readStream = new MemoryStream(packed, writable: false);
            var unpacked = archive.ReadAll(readStream, ArchiveFormat.Ustar);

            Assert.Equal(2, unpacked.Count);
            Assert.Equal(contentA, unpacked.Single(e => e.Name == "a.bin").Data);
            Assert.Equal(contentB, unpacked.Single(e => e.Name == "b.bin").Data);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void TarArchiveReader_ReadEntries_StreamsPayloads()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cpio_stream_read_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var fileA = Path.Combine(tmpDir, "a.bin");
            var contentA = BuildBytes(4096, 13);
            File.WriteAllBytes(fileA, contentA);

            var archive = new CpioArchive();
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                archive.SaveAll(ms, [BuildStreamedEntry("a.bin", fileA)], ArchiveFormat.Ustar, new WriteOptions());
                packed = ms.ToArray();
            }

            // 惰性流式读取：条目的负载经 DataStream 暴露，须在进入下一条目前消费。
            // Lazy streaming read: payload is exposed via DataStream and must be consumed
            // before the reader advances to the next entry.
            var reader = new TarArchiveReader();
            using var src = new MemoryStream(packed, writable: false);
            byte[] streamed = [];
            foreach (var entry in reader.ReadEntriesStreaming(src))
            {
                using var sink = new MemoryStream();
                Assert.NotNull(entry.DataStream);
                entry.DataStream!.CopyTo(sink);
                streamed = sink.ToArray();
            }

            Assert.Equal(contentA, streamed);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static ArchiveEntry BuildStreamedEntry(string name, string filePath)
    {
        return new ArchiveEntry
        {
            Name = name,
            LinkName = null,
            Data = [],
            DataStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read),
            Metadata = new ArchiveEntryMetadata
            {
                FileType = CpioFileType.Regular,
                Mode = 0x81A4, // S_IFREG | 0644
                UserId = 1000,
                GroupId = 1000,
                LinkCount = 1,
                ModificationTimeUnixSeconds = 1_700_000_100,
                UnixPermissions = 0x1A4,
            },
        };
    }

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