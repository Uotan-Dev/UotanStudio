using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Extraction;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Independently checks archive extraction failure and streaming semantics. <para>独立检查归档提取失败处理和流式语义。</para></summary>
public sealed class ParentExtractionReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-parent-review-" + Guid.NewGuid().ToString("N"));

    public ParentExtractionReviewTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CpioForwardHardlink_ReceivesLaterCarrierPayload()
    {
        using var archive = NewcArchive(
            NewcEntry("alias", [], inode: 1, links: 2),
            NewcEntry("carrier", "PAYLOAD"u8.ToArray(), inode: 1, links: 2));
        new CpioArchive().ExtractToDirectory(archive, _root, formatHint: ArchiveFormat.NewAscii);
        Assert.Equal("PAYLOAD", File.ReadAllText(Path.Combine(_root, "carrier")));
        Assert.Equal("PAYLOAD", File.ReadAllText(Path.Combine(_root, "alias")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CpioSelectedHardlink_ReceivesExcludedCarrierPayload(bool carrierFirst)
    {
        var alias = NewcEntry("alias", [], inode: 1, links: 2);
        var carrier = NewcEntry("carrier", "PAYLOAD"u8.ToArray(), inode: 1, links: 2);
        using var archive = carrierFirst ? NewcArchive(carrier, alias) : NewcArchive(alias, carrier);
        new CpioArchive().ExtractToDirectory(archive, _root,
            new ExtractOptions { IncludePatterns = ["alias"] }, ArchiveFormat.NewAscii);
        Assert.Equal("PAYLOAD", File.ReadAllText(Path.Combine(_root, "alias")));
        Assert.False(File.Exists(Path.Combine(_root, "carrier")));
    }

    [Fact]
    public void CpioCrcSymlink_AcceptsGnuZeroChecksum()
    {
        using var archive = NewcArchive(NewcEntry("link", "target"u8.ToArray(), mode: 0xa1ff, crc: true));
        Assert.Equal("link", Assert.Single(new CpioArchive().ListEntries(archive, ArchiveFormat.CrcAscii)).Name);
    }

    [Fact]
    public void CpioCrcRegularFile_RejectsIncorrectChecksum()
    {
        using var archive = NewcArchive(NewcEntry("file", "payload"u8.ToArray(), crc: true));
        Assert.Throws<InvalidDataException>(() => new CpioArchive().ListEntries(archive, ArchiveFormat.CrcAscii));
    }

    [Fact]
    public void CpioTruncatedStream_PreservesExistingDestination()
    {
        var target = Path.Combine(_root, "entry.bin");
        File.WriteAllText(target, "preserved");
        var entry = new ArchiveEntry
        {
            Name = "entry.bin",
            Data = [],
            DeclaredDataLength = 10,
            DataStreamFactory = () => new MemoryStream([1, 2]),
            Metadata = new ArchiveEntryMetadata { FileType = CpioFileType.Regular }
        };
        Assert.ThrowsAny<IOException>(() => new CpioExtractor().ExtractToDirectory([entry], _root,
            new ExtractOptions { Overwrite = true }));
        Assert.Equal("preserved", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void CpioNonSeekableSource_ExtractsPayloadsBeforeAdvancing()
    {
        using var archive = NewcArchive(NewcEntry("one", "ONE"u8.ToArray()), NewcEntry("two", "TWO"u8.ToArray()));
        using var source = new FragmentedReadStream(archive.ToArray(), seekable: false);
        new CpioArchive().ExtractToDirectory(source, _root, formatHint: ArchiveFormat.NewAscii);
        Assert.Equal("ONE", File.ReadAllText(Path.Combine(_root, "one")));
        Assert.Equal("TWO", File.ReadAllText(Path.Combine(_root, "two")));
    }

    [Fact]
    public void CpioFormatDetection_AcceptsShortReads()
    {
        using var archive = NewcArchive(NewcEntry("entry", "DATA"u8.ToArray()));
        using var source = new FragmentedReadStream(archive.ToArray(), seekable: true);
        Assert.Equal(ArchiveFormat.NewAscii, new CpioArchive().DetectFormat(source));
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void CpioNonSeekableSource_EnforcesSpoolingLimit()
    {
        using var archive = NewcArchive(NewcEntry("entry", "DATA"u8.ToArray()));
        using var source = new FragmentedReadStream(archive.ToArray(), seekable: false);
        var reader = new UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Formats.Cpio.CpioArchiveReader();
        Assert.ThrowsAny<IOException>(() => reader.ReadEntriesStreaming(source,
            ArchiveFormat.NewAscii, 16, TestContext.Current.CancellationToken).ToArray());
        Assert.True(source.Position <= 19);
    }

    [Fact]
    public void CpioNonSeekableSource_ObservesCancellationBeforeReading()
    {
        using var archive = NewcArchive(NewcEntry("entry", "DATA"u8.ToArray()));
        using var source = new FragmentedReadStream(archive.ToArray(), seekable: false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var reader = new UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Formats.Cpio.CpioArchiveReader();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.ReadEntriesStreaming(source,
            ArchiveFormat.NewAscii, 4096, cancellation.Token).ToArray());
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void AtomicFailure_PreservesDestinationAndRemovesTemporaryFile()
    {
        var target = Path.Combine(_root, "entry.bin");
        File.WriteAllText(target, "preserved");
        Assert.Throws<InvalidDataException>(() => FileExtraction.WriteAtomic(target, output =>
        {
            output.Write("partial"u8);
            throw new InvalidDataException("Independent payload verification failure.");
        }));
        Assert.Equal("preserved", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AtomicCancellationBeforeCommit_PreservesDestination()
    {
        var target = Path.Combine(_root, "entry.bin");
        File.WriteAllText(target, "preserved");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileExtraction.WriteAtomicAsync(target, output =>
        {
            output.Write("complete-but-canceled"u8);
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellationToken: cancellation.Token));
        Assert.Equal("preserved", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AtomicExtraction_RejectsExistingSymbolicLink(bool directoryLink)
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "entry.bin");
        File.WriteAllText(target, "preserved");
        var link = Path.Combine(_root, "link");
        try
        {
            if (directoryLink) Directory.CreateSymbolicLink(link, outside);
            else File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Skip("Symbolic links are unavailable: " + ex.Message);
        }
        try
        {
            var output = directoryLink ? Path.Combine(link, "entry.bin") : link;
            Assert.Throws<InvalidDataException>(() => FileExtraction.WriteAtomic(output, file => file.Write("changed"u8)));
            Assert.Equal("preserved", File.ReadAllText(target));
        }
        finally
        {
            if (directoryLink) Directory.Delete(link);
            else File.Delete(link);
        }
    }

    [Fact]
    public async Task OfpCancellationAtFirstEntry_PreservesDestination()
    {
        var target = Path.Combine(_root, "entry.bin");
        File.WriteAllText(target, "preserved");
        using var source = new MemoryStream(PlainMtkOfp());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new ImmediateProgress<OppExtractProgress>(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OppReader().ExtractFromStreamAsync(source,
            _root, new OppExtractOptions { Overwrite = true }, progress, cancellation.Token));
        Assert.Equal("preserved", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZipWrappedMtkOfp_UsesInnerFileSizeForTableOffsets(bool nestedDirectory)
    {
        using var source = new MemoryStream();
        using (var archive = new ZipArchive(source, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (nestedDirectory) archive.CreateEntry("nested/");
            using var entry = archive.CreateEntry(nestedDirectory ? "nested/inner.ofp" : "inner.ofp").Open();
            entry.Write(PlainMtkOfp());
        }
        source.Position = 0;
        var result = new OppReader().ExtractFromStream(source, _root);
        Assert.Equal(1, result.ExtractedFileCount);
        Assert.Equal(Enumerable.Repeat((byte)0x52, 16), File.ReadAllBytes(Path.Combine(_root, "entry.bin")));
    }

    [Fact]
    public async Task Nb0CancellationDuringFinalEntry_PropagatesAndPreservesDestination()
    {
        var target = Path.Combine(_root, "entry.bin");
        File.WriteAllText(target, "preserved");
        var data = new byte[68 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 16);
        "entry.bin"u8.CopyTo(data.AsSpan(20));
        data.AsSpan(68).Fill(0x52);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = new CancelOnPayloadReadStream(data, 68, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Nb0Extractor().ExtractFromStreamAsync(source,
            _root, new ExtractionOptions { Overwrite = true }, cancellationToken: cancellation.Token));
        Assert.Equal("preserved", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(_root));
    }

    private static byte[] PlainMtkOfp()
    {
        var result = new byte[16 + 0x60 + 0x6c];
        result.AsSpan(0, 16).Fill(0x52);
        var entry = result.AsSpan(16, 0x60);
        "entry.bin"u8.CopyTo(entry);
        BinaryPrimitives.WriteInt64LittleEndian(entry[40..], 16);
        var header = result.AsSpan(16 + 0x60);
        "REVIEW"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[72..], 1);
        ObfuscateMtkHeader(entry);
        ObfuscateMtkHeader(header);
        return result;
    }

    private static void ObfuscateMtkHeader(Span<byte> data)
    {
        ReadOnlySpan<byte> key = "geyixue"u8;
        for (var i = 0; i < data.Length; i++)
        {
            var value = data[i] ^ key[i % key.Length];
            data[i] = (byte)((value >> 4) | (value << 4));
        }
    }

    private static MemoryStream NewcArchive(params byte[][] entries)
        => new(entries.SelectMany(entry => entry).Concat(NewcEntry("TRAILER!!!", [])).ToArray());

    private static byte[] NewcEntry(string name, byte[] payload, uint inode = 0, uint links = 1,
        uint mode = 0x81a4, bool crc = false)
    {
        uint[] fields = [inode, mode, 0, 0, links, 0, (uint)payload.Length, 0, 0, 0, 0, (uint)name.Length + 1, 0];
        var header = Encoding.ASCII.GetBytes((crc ? "070702" : "070701") + string.Concat(fields.Select(f => f.ToString("x8"))) + name + "\0");
        using var result = new MemoryStream();
        result.Write(header);
        while (result.Length % 4 != 0) result.WriteByte(0);
        result.Write(payload);
        while (result.Length % 4 != 0) result.WriteByte(0);
        return result.ToArray();
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class FragmentedReadStream(byte[] data, bool seekable) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => seekable;
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 3));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 3)]);
    }

    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class CancelOnPayloadReadStream(byte[] data, long payloadOffset, CancellationTokenSource cancellation)
        : MemoryStream(data, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= payloadOffset) cancellation.Cancel();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}