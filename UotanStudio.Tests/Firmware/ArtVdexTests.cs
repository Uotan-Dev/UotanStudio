using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Art;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ArtVdexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-art-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static bool NativeEnabled => Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") == "1" &&
        Environment.GetEnvironmentVariable("UOTAN_VDEX_EXTRACTOR") is { Length: > 0 };
    public static bool SampleEnabled => NativeEnabled && Environment.GetEnvironmentVariable("UOTAN_ART_DEX_SAMPLE") is { Length: > 0 };

    public ArtVdexTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1, 10)]
    [InlineData(2, 10)]
    [InlineData(1, 6, 132)]
    [InlineData(1, 10, 132)]
    public async Task Extract_AllQuickenedOpcodes_RestoresOriginalDex(int count, int version = 6, int padding = 0)
    {
        var fixture = Fixture(count, version, padding);
        var source = Write("in.vdex", fixture.Vdex);
        var output = Path.Combine(_root, "out");
        var entries = VdexFile.Inspect(source, Token);
        Assert.Equal(count, entries.Count);
        await VdexFile.ExtractAsync(source, output, Token);
        foreach (var dex in Directory.GetFiles(output))
        {
            Assert.Equal(fixture.Dex, await File.ReadAllBytesAsync(dex, Token));
        }
        Assert.Equal(fixture.Vdex, await File.ReadAllBytesAsync(source, Token));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    public async Task Rebuild_ExtractedDex_ClearsCachesAndAllowsReextraction(int version)
    {
        var fixture = Fixture(2, version);
        var source = Write("in.vdex", fixture.Vdex);
        var dex = Write("replacement.dex", fixture.Dex);
        var target = Write("rebuilt.vdex", [1, 2, 3]);
        await VdexFile.RebuildAsync(source, [dex, dex], target, Token);
        var bytes = await File.ReadAllBytesAsync(target, Token);
        Assert.Equal(0u, U32(bytes, 16));
        Assert.Equal(version == 6 ? 0u : 8u, U32(bytes, 20));
        Assert.Equal(U32(fixture.Dex, 8), U32(bytes, 24));
        var output = Path.Combine(_root, "rebuilt");
        await VdexFile.ExtractAsync(target, output, Token);
        Assert.Equal(fixture.Dex, await File.ReadAllBytesAsync(Path.Combine(output, "classes2.dex"), Token));
    }

    [Theory]
    [InlineData("pc")]
    [InlineData("index")]
    [InlineData("extra")]
    [InlineData("truncated")]
    [InlineData("checksum")]
    [InlineData("signature")]
    [InlineData("leb")]
    [InlineData("count")]
    [InlineData("size")]
    public async Task Extract_MalformedInput_DoesNotPublish(string mutation)
    {
        var (bytes, dex) = Fixture(1);
        var quick = 28 + dex.Length + 3;
        switch (mutation)
        {
            case "pc": bytes[quick + 4] = 10; break;
            case "index": bytes[quick + 5] = 127; break;
            case "extra": Put32(bytes, quick, U32(bytes, quick) - 1); break;
            case "truncated": bytes = bytes[..^1]; break;
            case "checksum": bytes[28 + 8] ^= 1; break;
            case "signature": bytes[28 + 12] ^= 1; break;
            case "leb": bytes.AsSpan(quick + 4, 5).Fill(255); break;
            case "count": Put32(bytes, 8, uint.MaxValue); break;
            case "size": Put32(bytes, 28 + 32, uint.MaxValue); break;
        }
        var source = Write("bad.vdex", bytes);
        var output = Path.Combine(_root, "out");
        await Assert.ThrowsAsync<InvalidDataException>(() => VdexFile.ExtractAsync(source, output, Token));
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("009")]
    public void Inspect_UnsupportedVersion_ExplicitlyRejects(string version)
    {
        var bytes = Fixture(1).Vdex;
        System.Text.Encoding.ASCII.GetBytes(version).CopyTo(bytes, 4);
        Assert.Throws<NotSupportedException>(() => VdexFile.Inspect(Write("version.vdex", bytes), Token));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    public void Inspect_VerifierOnly019And021_ReturnEmptyExportSet(int version)
    {
        var source = Write($"verifier-{version}.vdex", VerifierOnlyFixture(version));

        Assert.Empty(VdexFile.Inspect(source, Token));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    public async Task Extract_VerifierOnly019And021_RejectsWithoutPublishing(int version)
    {
        var source = Write($"verifier-{version}.vdex", VerifierOnlyFixture(version));
        var output = Path.Combine(_root, "verifier-out");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            VdexFile.ExtractAsync(source, output, Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_MinimalCompactDex_ExportsStandardDexWithValidHashes()
    {
        var source = Write("minimal-compact.vdex", MinimalCompactVdexFixture());
        var output = Path.Combine(_root, "minimal-compact-out");

        await VdexFile.ExtractAsync(source, output, Token);

        var dex = await File.ReadAllBytesAsync(Path.Combine(output, "classes.dex"), Token);
        Assert.Equal("dex\n039\0"u8.ToArray(), dex[..8]);
        Assert.Equal((uint)dex.Length, U32(dex, 32));
        Assert.Equal(112u, U32(dex, 36));
        Assert.Equal(0x12345678u, U32(dex, 40));
        Assert.Equal(0u, U32(dex, 56));
        Assert.Equal(0u, U32(dex, 88));
        Assert.Equal(0u, U32(dex, 96));
        Assert.Equal(136u, U32(dex, 108));
        Assert.Equal((uint)dex.Length - 136u, U32(dex, 104));
    }

    [Theory]
    [InlineData("link")]
    [InlineData("class-table")]
    [InlineData("method-table")]
    [InlineData("class-data")]
    [InlineData("unknown-map")]
    [InlineData("quickening")]
    public async Task Extract_UnsupportedCompactDexStructures_RejectWithoutPublishing(string mutation)
    {
        var bytes = MinimalCompactVdexFixture();
        var compact = 40;
        var shared = compact + 136;
        switch (mutation)
        {
            case "link":
                Put32(bytes, compact + 44, 1);
                break;
            case "class-table":
                Put32(bytes, compact + 96, 1);
                Put32(bytes, compact + 100, 136);
                break;
            case "method-table":
                Put32(bytes, compact + 88, 1);
                Put32(bytes, compact + 92, 136);
                break;
            case "class-data":
                Put32(bytes, compact + 96, 1);
                Put32(bytes, compact + 100, 100);
                Put32(bytes, compact + 124, 52);
                break;
            case "unknown-map":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(shared + 16), 0x2007);
                break;
            case "quickening":
                Put32(bytes, 32, 1);
                Array.Resize(ref bytes, bytes.Length + 1);
                bytes[^1] = 0;
                break;
        }

        var source = Write($"bad-compact-{mutation}.vdex", bytes);
        var output = Path.Combine(_root, $"bad-compact-{mutation}-out");

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            VdexFile.ExtractAsync(source, output, Token));
        Assert.True(exception is InvalidDataException or NotSupportedException);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    public async Task Extract_CompactOffsetTableOutsideSharedSection_RejectsWithoutPublishing(int version)
    {
        var bytes = MinimalCompactVdexFixture();
        var shared = 40 + 136;
        Put32(bytes, shared + 12, uint.MaxValue);

        var source = Write($"bad-compact-table-{version}.vdex", bytes);
        var output = Path.Combine(_root, $"bad-compact-table-{version}-out");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            VdexFile.ExtractAsync(source, output, Token));
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(21)]
    public void Inspect_VerifierOnly019And021_RejectsSectionLengthMismatch(int version)
    {
        var bytes = VerifierOnlyFixture(version);
        Put32(bytes, 16, 1);

        Assert.Throws<InvalidDataException>(() =>
            VdexFile.Inspect(Write($"bad-verifier-{version}.vdex", bytes), Token));
    }

    [Fact]
    public async Task Extract_ExistingDestination_PreservesContents()
    {
        var source = Write("in.vdex", Fixture(1).Vdex);
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(output);
        var existing = Path.Combine(output, "classes.dex");
        await File.WriteAllBytesAsync(existing, [1, 2, 3], Token);
        await Assert.ThrowsAsync<IOException>(() => VdexFile.ExtractAsync(source, output, Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(existing, Token));
    }

    [Fact]
    public async Task Rebuild_InvalidReplacement_PreservesExistingOutput()
    {
        var fixture = Fixture(1);
        var source = Write("in.vdex", fixture.Vdex);
        fixture.Dex[8] ^= 1;
        var dex = Write("bad.dex", fixture.Dex);
        var output = Write("out.vdex", [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => VdexFile.RebuildAsync(source, [dex], output, Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(output, Token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Extract_SecondDexFails_DoesNotPublishFirstDex()
    {
        var fixture = Fixture(2);
        fixture.Vdex[32 + fixture.Dex.Length + 8] ^= 1;
        var output = Path.Combine(_root, "out");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            VdexFile.ExtractAsync(Write("in.vdex", fixture.Vdex), output, Token));
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root, "*.tmp"));
    }

    [Fact]
    public async Task ExtractAndRebuild_Cancelled_PreserveOutputs()
    {
        var fixture = Fixture(1);
        var source = Write("in.vdex", fixture.Vdex);
        var dex = Write("in.dex", fixture.Dex);
        var output = Write("out.vdex", [1, 2, 3]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VdexFile.ExtractAsync(source, Path.Combine(_root, "out"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VdexFile.RebuildAsync(source, [dex], output, cancellation.Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(output, Token));
        Assert.Empty(Directory.GetDirectories(_root, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("blob")]
    [InlineData("odd")]
    [InlineData("code")]
    [InlineData("sentinel")]
    public async Task Extract_Invalid010IndexTable_DoesNotPublish(string mutation)
    {
        var (bytes, dex) = Fixture(1, 10);
        var start = 28 + dex.Length;
        var table = start + (int)U32(bytes, bytes.Length - 4);
        switch (mutation)
        {
            case "table": Put32(bytes, bytes.Length - 4, uint.MaxValue); break;
            case "blob": Put32(bytes, table + 4, uint.MaxValue); break;
            case "odd": Put32(bytes, start, U32(bytes, start) - 1); break;
            case "code": Put32(bytes, table, 4); break;
            case "sentinel": bytes[start + 4 + (int)U32(bytes, start) - 1] = 0; break;
        }
        var output = Path.Combine(_root, "out");
        await Assert.ThrowsAsync<InvalidDataException>(() => VdexFile.ExtractAsync(Write("bad.vdex", bytes), output, Token));
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root, "*.tmp"));
    }

    [Fact]
    public async Task Extract_CancelDuringProcessing_RemovesStagedDexFiles()
    {
        var source = Write("large.vdex", Fixture(512).Vdex);
        var output = Path.Combine(_root, "out");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var operation = VdexFile.ExtractAsync(source, output, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(_root, "*.tmp"));
    }

    [Fact]
    public async Task Rebuild_CancelDuringProcessing_PreservesExistingOutput()
    {
        var fixture = Fixture(512);
        var source = Write("large.vdex", fixture.Vdex);
        var dex = Write("replacement.dex", fixture.Dex);
        var output = Write("out.vdex", [1, 2, 3]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var operation = VdexFile.RebuildAsync(source, Enumerable.Repeat(dex, 512).ToArray(), output, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(output, Token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory(Skip = "Requires UOTAN_NATIVE_TOOLS=1 and UOTAN_VDEX_EXTRACTOR pointing to a WSL vdexExtractor binary.",
        SkipUnless = nameof(NativeEnabled))]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(6, 132)]
    [InlineData(10, 132)]
    public async Task ExtractAndRebuild_VdexExtractor_ProducesIdenticalDex(int version, int padding = 0)
    {
        var fixture = Fixture(2, version, padding);
        var source = Write("input.vdex", fixture.Vdex);
        var native = Path.Combine(_root, "native");
        Directory.CreateDirectory(native);
        await RunNative(source, native);
        var extracted = Directory.GetFiles(native, "*.dex", SearchOption.AllDirectories);
        Assert.Equal(2, extracted.Length);
        foreach (var file in extracted)
        {
            Assert.Equal(fixture.Dex, await File.ReadAllBytesAsync(file, Token));
        }
        var managed = Path.Combine(_root, "managed");
        await VdexFile.ExtractAsync(source, managed, Token);
        var rebuilt = Path.Combine(_root, "rebuilt.vdex");
        await VdexFile.RebuildAsync(source,
            [Path.Combine(managed, "classes.dex"), Path.Combine(managed, "classes2.dex")], rebuilt, Token);
        var again = Path.Combine(_root, "native-rebuilt");
        Directory.CreateDirectory(again);
        await RunNative(rebuilt, again);
        Assert.Equal(2, Directory.GetFiles(again, "*.dex").Length);
        foreach (var file in Directory.GetFiles(again, "*.dex"))
        {
            Assert.Equal(fixture.Dex, await File.ReadAllBytesAsync(file, Token));
        }
    }

    private static async Task RunNative(string input, string output)
    {
        var info = new ProcessStartInfo("wsl.exe") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Environment.GetEnvironmentVariable("UOTAN_VDEX_EXTRACTOR")!);
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(WslPath(input));
        info.ArgumentList.Add("-o");
        info.ArgumentList.Add(WslPath(output));
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }

    [Fact(Skip = "Requires UOTAN_NATIVE_TOOLS=1, UOTAN_VDEX_EXTRACTOR and UOTAN_ART_DEX_SAMPLE; the sample is wrapped in a synthetic VDEX.",
        SkipUnless = nameof(SampleEnabled))]
    public async Task Extract_ExternalStandardDex_VdexExtractorAndManagedMatchSource()
    {
        var sample = Environment.GetEnvironmentVariable("UOTAN_ART_DEX_SAMPLE")!;
        var wrapped = Path.Combine(_root, "external.vdex");
        var header = new byte[28];
        "vdex006\0"u8.CopyTo(header);
        Put32(header, 8, 1);
        using (var input = File.OpenRead(sample))
        {
            Assert.InRange(input.Length, 112, uint.MaxValue);
            Put32(header, 12, (uint)input.Length);
            input.Position = 8;
            await input.ReadExactlyAsync(header.AsMemory(24, 4), Token);
            input.Position = 0;
            using var output = File.Create(wrapped);
            await output.WriteAsync(header, Token);
            await input.CopyToAsync(output, Token);
        }
        var managed = Path.Combine(_root, "managed-sample");
        await VdexFile.ExtractAsync(wrapped, managed, Token);
        var native = Path.Combine(_root, "native-sample");
        Directory.CreateDirectory(native);
        await RunNative(wrapped, native);
        using var originalStream = File.OpenRead(sample);
        var hash = await SHA256.HashDataAsync(originalStream, Token);
        foreach (var path in Directory.GetFiles(managed, "*.dex").Concat(Directory.GetFiles(native, "*.dex")))
        {
            using var stream = File.OpenRead(path);
            Assert.Equal(hash, await SHA256.HashDataAsync(stream, Token));
        }
        Assert.Single(Directory.GetFiles(native, "*.dex"));
    }

    private static string WslPath(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] VerifierOnlyFixture(int version)
    {
        var headerSize = version == 19 ? 20 : 28;
        const uint count = 2;
        const uint verifierSize = 5;
        var bootChecksums = version == 21 ? 3u : 0u;
        var classLoaderContext = version == 21 ? 4u : 0u;
        var bytes = new byte[headerSize + (int)count * 4 + (int)verifierSize +
            (int)bootChecksums + (int)classLoaderContext];
        Encoding.ASCII.GetBytes($"vdex{version:000}\0").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("000\0").CopyTo(bytes, 8);
        Put32(bytes, 12, count);
        Put32(bytes, 16, verifierSize);
        if (version == 21)
        {
            Put32(bytes, 20, bootChecksums);
            Put32(bytes, 24, classLoaderContext);
        }
        return bytes;
    }

    private static byte[] MinimalCompactVdexFixture()
    {
        const int headerSize = 20;
        const int dexSectionHeaderSize = 12;
        const int compactSize = 136;
        const int sharedSize = 52;
        var sectionHeaderOffset = headerSize + 4;
        var dexSectionOffset = sectionHeaderOffset + dexSectionHeaderSize + 4;
        var sharedOffset = dexSectionOffset + compactSize;
        var bytes = new byte[sharedOffset + sharedSize];
        "vdex019\0"u8.CopyTo(bytes.AsSpan(0));
        "002\0"u8.CopyTo(bytes.AsSpan(8));
        Put32(bytes, 12, 1);
        Put32(bytes, 16, 0);
        Put32(bytes, sectionHeaderOffset, compactSize + 4u);
        Put32(bytes, sectionHeaderOffset + 4, sharedSize);
        Put32(bytes, sectionHeaderOffset + 8, 0);

        var compact = bytes.AsSpan(dexSectionOffset, compactSize);
        "cdex001\0"u8.CopyTo(compact);
        Put32(compact, 32, compactSize);
        Put32(compact, 36, 136);
        Put32(compact, 40, 0x12345678);
        Put32(compact, 52, 0);
        Put32(compact, 104, sharedSize);
        Put32(compact, 108, compactSize);
        Put32(compact, 112, 0);
        Put32(compact, 116, 0);
        Put32(compact, 120, 0);
        Put32(compact, 124, 0);
        Put32(compact, 128, 0);
        Put32(compact, 132, 0);

        var shared = bytes.AsSpan(sharedOffset, sharedSize);
        Put32(shared, 0, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(shared[4..], 0);
        Put32(shared, 8, 1);
        Put32(shared, 12, 28);
        BinaryPrimitives.WriteUInt16LittleEndian(shared[16..], 0x1000);
        Put32(shared, 20, 1);
        Put32(shared, 24, 40);
        return bytes;
    }

    internal static (byte[] Vdex, byte[] Dex) Fixture(int count, int version = 6, int padding = 0)
    {
        var dex = SmaliAssembler.Assemble($$"""
            .class public Lexample/Test;
            .super Ljava/lang/Object;
            .field public field:I
            .field public wide:J
            .field public object:Ljava/lang/Object;
            .field public boolean:Z
            .field public byte:B
            .field public char:C
            .field public short:S
            .method public static run(Lexample/Test;)V
                .registers 8
                {{string.Concat(Enumerable.Repeat("nop\n", padding))}}
                iget v0, p0, Lexample/Test;->field:I
                iput v0, p0, Lexample/Test;->field:I
                iget-wide v0, p0, Lexample/Test;->wide:J
                iput-wide v0, p0, Lexample/Test;->wide:J
                iget-object v0, p0, Lexample/Test;->object:Ljava/lang/Object;
                iput-object v0, p0, Lexample/Test;->object:Ljava/lang/Object;
                invoke-virtual {p0}, Ljava/lang/Object;->toString()Ljava/lang/String;
                invoke-virtual/range {p0 .. p0}, Ljava/lang/Object;->toString()Ljava/lang/String;
                iget-boolean v0, p0, Lexample/Test;->boolean:Z
                iput-boolean v0, p0, Lexample/Test;->boolean:Z
                iget-byte v0, p0, Lexample/Test;->byte:B
                iput-byte v0, p0, Lexample/Test;->byte:B
                iget-char v0, p0, Lexample/Test;->char:C
                iput-char v0, p0, Lexample/Test;->char:C
                iget-short v0, p0, Lexample/Test;->short:S
                iput-short v0, p0, Lexample/Test;->short:S
                check-cast p0, Lexample/Test;
                nop
                return-void
            .end method
            .method public static zHelper()V
                .registers 0
                return-void
            .end method
            .method public native zNative()V
            .end method
            .method public zVirtual()V
                .registers 1
                return-void
            .end method
            .method public static zPacked(I)V
                .registers 1
                packed-switch p0, :table
                return-void
            :target
                return-void
            :table
                .packed-switch 0
                    :target
                .end packed-switch
            .end method
            .method public static zSparse(I)V
                .registers 1
                sparse-switch p0, :table
                return-void
            :target
                return-void
            :table
                .sparse-switch
                    42 -> :target
                .end sparse-switch
            .end method
            .method public static zArray([B)V
                .registers 1
                fill-array-data p0, :table
                return-void
            :table
                .array-data 1
                    0x73
                    0xe3
                    0xea
                .end array-data
            .end method
            """).ToArray();
        var quickened = (byte[])dex.Clone();
        var classData = (int)U32(dex, (int)U32(dex, 100) + 24);
        var fields = ReadLeb(dex, ref classData) + ReadLeb(dex, ref classData);
        var methods = ReadLeb(dex, ref classData) + ReadLeb(dex, ref classData);
        for (var i = 0; i < fields; i++)
        {
            _ = ReadLeb(dex, ref classData);
            _ = ReadLeb(dex, ref classData);
        }
        var codeItems = new List<int>();
        for (var i = 0; i < methods; i++)
        {
            _ = ReadLeb(dex, ref classData);
            _ = ReadLeb(dex, ref classData);
            var code = ReadLeb(dex, ref classData);
            if (code != 0)
            {
                codeItems.Add(code);
            }
        }
        var blobs = new List<byte[]>();
        byte[] normal = [0x52, 0x53, 0x54, 0x59, 0x5a, 0x5b, 0x6e, 0x74, 0x5c, 0x5d, 0x5e, 0x5f, 0x55, 0x56, 0x57, 0x58];
        foreach (var code in codeItems)
        {
            var quick = new List<byte>();
            var quick010 = new List<byte>();
            var units = U32(dex, code + 12);
            for (var pc = 0; pc < units;)
            {
                var offset = code + 16 + pc * 2;
                var op = dex[offset];
                var size = OpCodes.GetSize(op);
                var word = BinaryPrimitives.ReadUInt16LittleEndian(dex.AsSpan(offset));
                if (word is 0x100 or 0x200 or 0x300)
                {
                    var n = BinaryPrimitives.ReadUInt16LittleEndian(dex.AsSpan(offset + 2));
                    size = word switch
                    {
                        0x100 => 4 + 2 * n,
                        0x200 => 2 + 4 * n,
                        _ => 4 + (n * (int)U32(dex, offset + 4) + 1) / 2
                    };
                }
                var quickIndex = Array.IndexOf(normal, op);
                if (quickIndex >= 0)
                {
                    quickened[offset] = (byte)(0xe3 + quickIndex);
                    AddLeb(quick, pc);
                    AddLeb(quick, BinaryPrimitives.ReadUInt16LittleEndian(dex.AsSpan(offset + 2)));
                    quick010.AddRange(dex.AsSpan(offset + 2, 2).ToArray());
                    quickened[offset + 2] = 123;
                    quickened[offset + 3] = 0;
                }
                else if (op == 0x1f)
                {
                    AddLeb(quick, pc);
                    AddLeb(quick, dex[offset + 1]);
                    AddLeb(quick, pc);
                    AddLeb(quick, BinaryPrimitives.ReadUInt16LittleEndian(dex.AsSpan(offset + 2)));
                    quick010.Add(dex[offset + 1]);
                    quick010.Add(0);
                    quick010.AddRange(dex.AsSpan(offset + 2, 2).ToArray());
                    quickened.AsSpan(offset, 4).Clear();
                }
                else if (op == 0x0e)
                {
                    quickened[offset] = 0x73;
                }
                else if (op == 0)
                {
                    quick010.AddRange([255, 255]);
                }
                pc += size;
            }
            blobs.Add((version == 6 ? quick : quick010).ToArray());
        }
        var header = 24 + 4 * count;
        var blobBytesPerDex = blobs.Sum(b => 4 + b.Length);
        if (version == 10)
        {
            var quickStart = header + dex.Length * count;
            var tables = blobBytesPerDex * count;
            var quickSize = tables + count * (8 * codeItems.Count + 4);
            var output = new byte[quickStart + quickSize];
            "vdex010\0"u8.CopyTo(output);
            Put32(output, 8, (uint)count);
            Put32(output, 12, (uint)(dex.Length * count));
            Put32(output, 20, (uint)quickSize);
            for (var i = 0; i < count; i++)
            {
                Put32(output, 24 + i * 4, U32(dex, 8));
                quickened.CopyTo(output, header + i * dex.Length);
                var blob = blobBytesPerDex * i;
                var table = tables + i * 8 * codeItems.Count;
                for (var j = 0; j < codeItems.Count; j++)
                {
                    Put32(output, quickStart + blob, (uint)blobs[j].Length);
                    blobs[j].CopyTo(output, quickStart + blob + 4);
                    Put32(output, quickStart + table + j * 8, (uint)codeItems[j]);
                    Put32(output, quickStart + table + j * 8 + 4, (uint)blob);
                    blob += 4 + blobs[j].Length;
                }
                Put32(output, quickStart + tables + count * codeItems.Count * 8 + i * 4, (uint)table);
            }
            return (output, dex);
        }
        var bytes = new byte[header + dex.Length * count + 3 + blobBytesPerDex * count];
        "vdex006\0"u8.CopyTo(bytes);
        Put32(bytes, 8, (uint)count);
        Put32(bytes, 12, (uint)(dex.Length * count));
        Put32(bytes, 16, 3);
        Put32(bytes, 20, (uint)(blobBytesPerDex * count));
        for (var i = 0; i < count; i++)
        {
            Put32(bytes, 24 + 4 * i, U32(dex, 8));
            quickened.CopyTo(bytes, header + dex.Length * i);
            var at = header + dex.Length * count + 3 + blobBytesPerDex * i;
            foreach (var blob in blobs)
            {
                Put32(bytes, at, (uint)blob.Length);
                blob.CopyTo(bytes, at + 4);
                at += 4 + blob.Length;
            }
        }
        return (bytes, dex);
    }

    private static int ReadLeb(byte[] bytes, ref int offset)
    {
        var result = 0;
        var shift = 0;
        byte value;
        do
        {
            value = bytes[offset++];
            result |= (value & 127) << shift;
            shift += 7;
        } while ((value & 128) != 0);
        return result;
    }

    private static void AddLeb(List<byte> bytes, int value)
    {
        do
        {
            var b = (byte)(value & 127);
            value >>= 7;
            bytes.Add(value == 0 ? b : (byte)(b | 128));
        } while (value != 0);
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);

    public void Dispose() => Directory.Delete(_root, true);
}