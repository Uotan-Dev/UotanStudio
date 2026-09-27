using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Dat.Reader;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.ImgDiff;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ImgDiffBuilderTests
{
    private const int Block = 4096;

    [Fact]
    public void Build_ShorterSource_EmitsNormalAndRawChunksThatApplyByteExact()
    {
        var source = Ramp(2, 0x11);
        var target = Concat(Ramp(2, 0x22), Ramp(1, 0x33));
        using var patch = new MemoryStream();
        ImageDeltaBuilder.Build(new MemoryStream(source), new MemoryStream(target), patch,
            cancellationToken: TestContext.Current.CancellationToken);
        var bytes = patch.ToArray();
        Assert.Equal("IMGDIFF2"u8.ToArray(), bytes[..8]);
        Assert.Equal(2, BitConverter.ToInt32(bytes, 8));
        Assert.Equal(0, BitConverter.ToInt32(bytes, 12));
        Assert.Equal(0L, BitConverter.ToInt64(bytes, 16));
        Assert.Equal((long)source.Length, BitConverter.ToInt64(bytes, 24));
        Assert.Equal(48 + Block, (int)BitConverter.ToInt64(bytes, 32));
        Assert.Equal(3, BitConverter.ToInt32(bytes, 40));
        Assert.Equal(Block, BitConverter.ToInt32(bytes, 44));
        Assert.Equal(target[(2 * Block)..], bytes[48..(48 + Block)]);
        using var output = new MemoryStream();
        patch.Position = 0;
        ImageDeltaPatch.Apply(new MemoryStream(source), patch, output, target.LongLength);
        Assert.Equal(target, output.ToArray());
    }

    [Fact]
    public void Build_EmptySource_EmitsRawOnlyContainer()
    {
        var target = Ramp(2, 0x55);
        using var patch = new MemoryStream();
        ImageDeltaBuilder.Build(new MemoryStream(), new MemoryStream(target), patch,
            cancellationToken: TestContext.Current.CancellationToken);
        var bytes = patch.ToArray();
        Assert.Equal("IMGDIFF2"u8.ToArray(), bytes[..8]);
        Assert.Equal(1, BitConverter.ToInt32(bytes, 8));
        Assert.Equal(3, BitConverter.ToInt32(bytes, 12));
        Assert.Equal(Block * 2, BitConverter.ToInt32(bytes, 16));
        using var output = new MemoryStream();
        patch.Position = 0;
        ImageDeltaPatch.Apply(new MemoryStream(), patch, output, target.LongLength);
        Assert.Equal(target, output.ToArray());
    }

    [Fact]
    public void Build_ThroughDatReader_RebuildsTargetByteExact()
    {
        var source = Ramp(2, 0x66);
        var target = Ramp(3, 0x77);
        using var patch = new MemoryStream();
        ImageDeltaBuilder.Build(new MemoryStream(source), new MemoryStream(target), patch,
            cancellationToken: TestContext.Current.CancellationToken);
        var patchBytes = patch.ToArray();
        using var temp = new TempDirectory();
        var list = temp.Write("list", $"4\n3\n0\n0\nimgdiff 0 {patchBytes.Length} {Sha1(source)} {Sha1(target)} 2,0,3 2 2,0,2 2,0,2\n");
        var output = Path.Combine(temp.Path, "out.img");
        DatReader.Unpack(list, temp.WriteBytes("new.dat", []), temp.WriteBytes("patch.dat", patchBytes), temp.WriteBytes("old.img", source), output);
        Assert.Equal(target, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_LargeMostlyZeroImages_RoundTripAndSelfVerifyWithCompactPatch()
    {
        var source = new byte[1024 * Block];
        var target = new byte[1024 * Block];
        var random = new Random(42);
        random.NextBytes(source);
        source.AsSpan().Clear();
        source.AsSpan(3 * Block, Block).Fill(0xAB);
        source.AsSpan(500 * Block, Block).Fill(0xCD);
        source.AsSpan().CopyTo(target);
        target.AsSpan(97 * Block, 2 * Block).Fill(0xEF);
        using var patch = new MemoryStream();
        ImageDeltaBuilder.Build(new MemoryStream(source), new MemoryStream(target), patch, Block, new ImgDiffBuilderOptions { SelfVerify = true },
            TestContext.Current.CancellationToken);
        Assert.True(patch.Length < source.LongLength / 8);
        patch.Position = 0;
        using var output = new MemoryStream();
        ImageDeltaPatch.Apply(new MemoryStream(source), patch, output, target.LongLength);
        Assert.Equal(target, output.ToArray());
    }

    [Fact]
    public void Build_NonPowerOfTwoBlockSize_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            ImageDeltaBuilder.Build(new MemoryStream(), new MemoryStream(), new MemoryStream(), 3000,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Build_UnseekableSource_ThrowsArgumentException()
    {
        using var source = new NoSeekStream(Ramp(1, 0x01));
        Assert.Throws<ArgumentException>(() =>
            ImageDeltaBuilder.Build(source, new MemoryStream(Ramp(1, 0x02)), new MemoryStream(),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Build_CancellationRequested_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ImageDeltaBuilder.Build(new MemoryStream(Ramp(1, 0x03)), new MemoryStream(Ramp(1, 0x04)), new MemoryStream(),
                Block, new ImgDiffBuilderOptions(), cancellation.Token));
    }

    [Fact]
    public void Build_SelfVerifyOnNonSeekableOutput_ThrowsArgumentException()
    {
        using var output = new NoSeekStream();
        Assert.Throws<ArgumentException>(() =>
            ImageDeltaBuilder.Build(new MemoryStream(Ramp(1, 0x05)), new MemoryStream(Ramp(1, 0x06)), output,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    private static byte[] Ramp(int blocks, byte fill)
    {
        var data = new byte[blocks * Block];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(fill ^ i);
        }

        return data;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private sealed class NoSeekStream(byte[]? data = null) : Stream
    {
        private readonly MemoryStream _inner = new(data ?? [], writable: true);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}