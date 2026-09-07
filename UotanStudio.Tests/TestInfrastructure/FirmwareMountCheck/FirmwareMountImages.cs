using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;

namespace UotanStudio.Tests.Firmware;

internal static class FirmwareMountImages
{
    internal static void Create(string outputRoot, IReadOnlyCollection<string> requestedCases)
    {
        var source = Path.Combine(outputRoot, "source");
        var images = Path.Combine(outputRoot, "images");
        var reference = Path.Combine(outputRoot, "reference");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(reference);
        Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        Directory.CreateDirectory(Path.Combine(source, "indexed"));
        File.WriteAllText(Path.Combine(source, "hello.txt"), "Firmware kernel mount verification.\n", new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(source, "inline.bin"), Enumerable.Range(0, 100).Select(value => (byte)value).ToArray());
        File.WriteAllText(Path.Combine(source, "nested", "name with spaces.txt"), "A path with spaces.\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(source, "nested", "\u4e2d\u6587.txt"), "UTF-8 name and content: \u56fa\u4ef6\n", new UTF8Encoding(false));
        var random = new byte[128 * 1024 + 7];
        new Random(20260907).NextBytes(random);
        File.WriteAllBytes(Path.Combine(source, "nested", "random.bin"), random);
        using (var file = File.Create(Path.Combine(source, "large.bin")))
        {
            var pattern = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0123456789abcdef: firmware mount conformance\n", 1700)));
            var remaining = 5 * 1024 * 1024 + 123;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, pattern.Length);
                file.Write(pattern.AsSpan(0, count));
                remaining -= count;
            }
        }
        using (var file = File.Create(Path.Combine(source, "sparse.bin")))
        {
            file.SetLength(1024 * 1024 + 19);
            file.Position = 65535;
            file.Write("sparse-boundary"u8);
        }
        for (var i = 0; i < 600; i++)
            File.WriteAllText(Path.Combine(source, "indexed", $"entry-{i:D4}.txt"), $"entry:{i:D4}\n", new UTF8Encoding(false));

        string Relative(string path) => "./" + Path.GetRelativePath(source, path).Replace('\\', '/');
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(Relative, StringComparer.Ordinal).ToArray();
        var hashes = new List<string>();
        foreach (var path in files)
        {
            using var stream = File.OpenRead(path);
            hashes.Add($"{Convert.ToHexStringLower(SHA256.HashData(stream))}  {Relative(path)}");
        }
        void WriteLines(string path, IEnumerable<string> lines) => File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        WriteLines(Path.Combine(reference, "SHA256SUMS"), hashes);
        WriteLines(Path.Combine(reference, "files.txt"), files.Select(Relative));
        WriteLines(Path.Combine(reference, "directories.txt"), new[] { "." }.Concat(Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Select(Relative)).Order(StringComparer.Ordinal));

        var cases = new List<string>();
        void Build(string name, string format, string mountOptions, Action<string> build)
        {
            if (!requestedCases.Contains(name)) return;
            var path = Path.Combine(images, name + ".img");
            build(path);
            using var image = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(image));
            cases.Add($"{name}\t{format}\t{mountOptions}\t{hash}\t{image.Length}");
        }
        Build("ext4-default", "ext4", "ro,noload", path => Ext4Builder.Build(source, path));
        Build("ext4-no-journal", "ext4", "ro,noload", path => Ext4Builder.Build(source, path, new Ext4Options { IncludeJournal = false }));
        Build("erofs-flat", "erofs", "ro", path => new ErofsBuilder().Build(source, path, new ErofsOptions { Compress = false }));
        foreach (var algorithm in Enum.GetValues<ErofsCompressionAlgorithm>())
            Build("erofs-" + algorithm.ToString().ToLowerInvariant(), "erofs", "ro", path => new ErofsBuilder().Build(source, path, new ErofsOptions { Algorithm = algorithm }));
        Build("erofs-lz4-full-index", "erofs", "ro", path => new ErofsBuilder().Build(source, path, new ErofsOptions { UseCompactedIndexes = false }));
        Build("erofs-fragments-dedup", "erofs", "ro", path => new ErofsBuilder().Build(source, path, new ErofsOptions { Fragments = true, Dedup = true }));
        Build("erofs-metabox", "erofs", "ro", path => new ErofsBuilder().Build(source, path, new ErofsOptions { Metabox = true }));
        Build("f2fs-flat", "f2fs", "ro,norecovery", path => F2fsBuilder.Build(source, path));
        foreach (var algorithm in new[] { CompressionAlgorithm.Lz4, CompressionAlgorithm.Zstd, CompressionAlgorithm.Lzo })
            Build("f2fs-" + algorithm.ToString().ToLowerInvariant(), "f2fs", "ro,norecovery", path => F2fsBuilder.Build(source, path, new F2fsOptions { EnableCompression = true, CompressionAlgorithm = algorithm }));
        WriteLines(Path.Combine(outputRoot, "cases.tsv"), cases);
    }
}