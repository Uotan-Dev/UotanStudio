using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Compression;

namespace UotanStudio.Tests.Firmware;

internal static class FirmwareMountImages
{
    internal static void Create(string outputRoot, IReadOnlyCollection<string> requestedCases)
    {
        CreateSource(outputRoot);
        var source = Path.Combine(outputRoot, "source");
        var images = Path.Combine(outputRoot, "images");
        Directory.CreateDirectory(images);
        BuildImages(outputRoot, source, images, requestedCases);
    }

    internal static void CreateSource(string outputRoot)
    {
        var source = Path.Combine(outputRoot, "source");
        var reference = Path.Combine(outputRoot, "reference");
        Directory.CreateDirectory(source);
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
    }

    private static void BuildImages(string outputRoot, string source, string images, IReadOnlyCollection<string> requestedCases)
    {
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
        // Device-table (multi-device) EROFS is an incompatible superblock feature that the Linux kernel
        // only learned to mount from 5.13 onward, so this case is never added to the era-5.10 matrix and
        // is mounted only by a device-table-capable guest. Extra devices are declared (tags + block
        // counts) while all content stays on device 0, matching the builder's declaration-only mode.
        // 设备表（多设备）EROFS 是内核从 5.13 起才支持挂载的不兼容超级块特性，因此本用例从不进入
        // 5.10 矩阵，只由支持设备表的客户机挂载。额外设备仅声明（tag 与块数），全部内容仍在设备 0，
        // 与打包器的“仅声明”模式一致。
        Build("erofs-multi-device", "erofs", "ro", path => new ErofsBuilder().Build(source, path, new ErofsOptions
        {
            Compress = false,
            ExtraDeviceCount = 2,
            ExtraDeviceImages = [new ErofsDeviceSpec("uotan-a", 8192), new ErofsDeviceSpec("uotan-b", 16384)],
        }));
        // Legacy compact-inode image: the era-matched GKI 5.10 kernel rejects the modern default
        // writer output (dot-omitted / nlink-1 i_format bit and, for device tables, the superblock
        // incompat bit — both post-5.10). LegacyCompatibility is the 5.10-mountable path and doubles
        // as the regression check that the multi-device superblock edits, which are all behind
        // <c>extraDevices > 0</c>, leave the ordinary single-device layout byte-identical.
        // legacy 紧凑 inode 镜像：时代匹配的 GKI 5.10 内核会拒绝现代默认输出（dot-omitted / nlink-1
        // 的 i_format 位，以及设备表的超级块不兼容位——两者都晚于 5.10）。LegacyCompatibility 是可被
        // 5.10 挂载的路径，同时用作回归检查：全部位于 <c>extraDevices > 0</c> 之后的多设备超级块改动
        // 不会改变常规单设备布局的字节。
        Build("erofs-legacy-flat", "erofs", "ro", path => new ErofsBuilder().Build(source, path, new ErofsOptions { Compress = false, LegacyCompatibility = true }));
        Build("f2fs-flat", "f2fs", "ro,norecovery", path => F2fsBuilder.Build(source, path));
        foreach (var algorithm in new[] { CompressionAlgorithm.Lz4, CompressionAlgorithm.Zstd, CompressionAlgorithm.Lzo })
            Build("f2fs-" + algorithm.ToString().ToLowerInvariant(), "f2fs", "ro,norecovery", path => F2fsBuilder.Build(source, path, new F2fsOptions { EnableCompression = true, CompressionAlgorithm = algorithm }));
        File.WriteAllText(Path.Combine(outputRoot, "cases.tsv"), string.Join('\n', cases) + "\n", new UTF8Encoding(false));
    }
}