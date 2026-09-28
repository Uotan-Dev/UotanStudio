using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.VendorFirmware.Dz;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class KdzExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-kdz-" + Guid.NewGuid().ToString("N"));

    public KdzExtractorTests() => Directory.CreateDirectory(_root);

    public static bool ToolsEnabled => Environment.GetEnvironmentVariable("UOTAN_KDZTOOLS") is { Length: > 0 };

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Parse_KnownVersionAndTerminator_PreservesEveryEntry(int version, bool finalMarker)
    {
        var bytes = Build(version, 3, finalMarker);
        var path = Write(bytes);

        var result = KdzExtractor.Parse(path);

        Assert.Equal(version, result.HeaderType);
        Assert.Equal(new[] { "part0.bin", "part1.bin", "part2.bin" }, result.Partitions.Select(p => p.Name));
        Assert.Equal(new ulong[] { 1, 1, 1 }, result.Partitions.Select(p => p.Length));
        Assert.Equal(3, KdzExtractor.Extract(path, Path.Combine(_root, "out"),
            cancellationToken: TestContext.Current.CancellationToken));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(new[] { (byte)(i + 16) }, File.ReadAllBytes(Path.Combine(_root, "out", $"part{i}.bin")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_SingleEntry_PreservesEntry(bool finalMarker)
    {
        Assert.Equal("part0.bin", Assert.Single(KdzExtractor.Parse(Write(Build(2, 1, finalMarker))).Partitions).Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(279)]
    [InlineData(280)]
    [InlineData(281)]
    [InlineData(552)]
    public void Parse_TruncatedHeaderOrFinalRecord_ThrowsInvalidData(int length)
    {
        var bytes = Build(2, 2, true)[..length];
        if (length >= 280)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(264), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(272), (ulong)length);
        }

        Assert.Throws<InvalidDataException>(() => KdzExtractor.Parse(Write(bytes)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_MissingTerminatorAtPayloadOrEof_ThrowsInvalidData(bool eof)
    {
        var bytes = Build(2, 1, false);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(272), 280);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(264), eof ? 0ul : 1ul);
        bytes = bytes[..(eof ? 280 : 281)];

        Assert.Throws<InvalidDataException>(() => KdzExtractor.Parse(Write(bytes)));
    }

    [Theory]
    [InlineData(8ul)]
    [InlineData(279ul)]
    [InlineData(280ul)]
    [InlineData(552ul)]
    [InlineData(ulong.MaxValue)]
    public void Parse_PayloadOverlapsTableOrExceedsFile_ThrowsInvalidData(ulong offset)
    {
        var bytes = Build(2, 2, true);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(272), offset);

        Assert.Throws<InvalidDataException>(() => KdzExtractor.Parse(Write(bytes)));
    }

    [Fact]
    public void Parse_OverflowingLength_ThrowsInvalidData()
    {
        var bytes = Build(2, 1, false);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(264), ulong.MaxValue);
        Assert.Throws<InvalidDataException>(() => KdzExtractor.Parse(Write(bytes)));
    }

    [Fact]
    public void Parse_FinalRecordEndsAtEof_AcceptsZeroLengthPayload()
    {
        var bytes = Build(2, 1, true)[..281];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(265), 0);
        Assert.Equal(0ul, Assert.Single(KdzExtractor.Parse(Write(bytes)).Partitions).Length);
    }

    [Fact]
    public void Parse_TooManyEntries_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => KdzExtractor.Parse(Write(Build(2, 65537, true))));
    }

    [Fact]
    public void Parse_EntryCountAtLimit_AcceptsEveryEntry()
    {
        Assert.Equal(65536, KdzExtractor.Parse(Write(Build(2, 65536, true))).Partitions.Count);
    }

    [Fact]
    public void Parse_MissingMarkerWithNonzeroPadding_ThrowsInvalidData()
    {
        var bytes = Build(2, 1, false);
        bytes[280] = 0x41;
        Assert.Throws<InvalidDataException>(() => KdzExtractor.Parse(Write(bytes)));
    }

    [Fact]
    public void Parse_EmptyTable_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => KdzExtractor.Parse(Write([.. DzConstants.KdzHeaderV2, 0])));
    }

    [Theory(Skip = "Set UOTAN_KDZTOOLS to a local kdztools checkout and provide Python on PATH.",
        SkipUnless = nameof(ToolsEnabled))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Extract_IndependentMkkdzOutput_MatchesUnkdzAndSourceHashes(bool reverseHeaders)
    {
        var token = TestContext.Current.CancellationToken;
        var tools = Environment.GetEnvironmentVariable("UOTAN_KDZTOOLS")!;
        var source = new byte[8193];
        new Random(1729).NextBytes(source);
        await File.WriteAllBytesAsync(Path.Combine(_root, "firmware.dz"), source, token);
        await File.WriteAllBytesAsync(Path.Combine(_root, "helper.dll"), source[..4097], token);
        await File.WriteAllTextAsync(Path.Combine(_root, ".kdz.params"),
            $"version=2\ndataStart=4096\npayload0=firmware.dz\npayload0head={(reverseHeaders ? 1 : 0)}\n" +
            $"payload1=helper.dll\npayload1head={(reverseHeaders ? 0 : 1)}\n", token);
        var path = Path.Combine(_root, "independent.kdz");
        await RunToolAsync(Path.Combine(tools, "mkkdz.py"), ["-f", path, "-m", "-d", _root], token);
        var result = KdzExtractor.Parse(path);
        Assert.Equal(2, result.HeaderType);
        Assert.Equal(reverseHeaders ? new[] { "helper.dll", "firmware.dz" } : ["firmware.dz", "helper.dll"],
            result.Partitions.Select(p => p.Name));
        var ours = Path.Combine(_root, "ours");
        var independent = Path.Combine(_root, "independent");
        Assert.Equal(2, KdzExtractor.Extract(path, ours, cancellationToken: token));
        await RunToolAsync(Path.Combine(tools, "unkdz.py"), ["-f", path, "-x", "-d", independent], token);
        foreach (var entry in result.Partitions)
        {
            var expected = await File.ReadAllBytesAsync(Path.Combine(_root, entry.Name), token);
            Assert.Equal((ulong)expected.Length, entry.Length);
            Assert.Equal(entry.Name == "firmware.dz" ? 4096ul : 12289ul, entry.Offset);
            Assert.Equal(SHA256.HashData(expected),
                SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(ours, entry.Name), token)));
            Assert.Equal(SHA256.HashData(expected),
                SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(independent, entry.Name), token)));
        }
    }

    private static async Task RunToolAsync(string script, string[] arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("python")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static byte[] Build(int version, int count, bool finalMarker)
    {
        var dataStart = 8 + count * 272 + 1;
        var bytes = new byte[dataStart + count];
        var magic = version switch
        {
            0 => DzConstants.KdzHeaderV0,
            1 => DzConstants.KdzHeaderV1,
            _ => DzConstants.KdzHeaderV2
        };
        magic.CopyTo(bytes, 0);
        var position = 8;
        for (var i = 0; i < count; i++)
        {
            if (finalMarker && i == count - 1)
            {
                bytes[position++] = 3;
            }

            Encoding.ASCII.GetBytes($"part{i}.bin").CopyTo(bytes, position);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(position + 256), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(position + 264), (ulong)(dataStart + i));
            bytes[dataStart + i] = (byte)(i + 16);
            position += 272;
        }

        return bytes;
    }

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_root, "fixture.kdz");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, true);
}