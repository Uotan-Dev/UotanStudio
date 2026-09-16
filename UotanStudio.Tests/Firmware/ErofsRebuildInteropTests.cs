using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Rebuilds independently generated EROFS images after edits and checks native readers.</summary>
[Collection("Workspace state")]
public sealed class ErofsRebuildInteropTests
{
    private static string ToolsRoot => Environment.GetEnvironmentVariable("UOTAN_EROFS_TOOLS") ?? "/usr";
    private static string Tool(string name) => WslTestHelpers.Quote(ToolsRoot + "/bin/" + name);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("flat", "", 4096)]
    [InlineData("lz4", "-zlz4", 4096)]
    [InlineData("lz4hc", "-zlz4hc,12 -C16384", 4096)]
    [InlineData("lzma", "-zlzma,6,dictsize=1048576", 4096)]
    [InlineData("deflate", "-zdeflate", 4096)]
    [InlineData("zstd", "-zzstd,3,dictsize=65536", 4096)]
    [InlineData("lz4-512", "-zlz4", 512)]
    [InlineData("flat-1024", "", 1024)]
    [InlineData("lz4-full", "-zlz4 -Elegacy-compress", 4096)]
    [InlineData("mixed", "-zlz4:lzma,6:deflate,6:zstd,3", 4096)]
    [InlineData("lz4-fragments", "-zlz4 -Efragments,dedupe", 4096)]
    [InlineData("lz4-ztail", "-zlz4 -Eztailpacking", 4096)]
    [InlineData("lz4-times", "-zlz4 --preserve-mtime", 4096)]
    [InlineData("deflate-window", "-zdeflate,dictsize=4096", 4096)]
    [InlineData("lzma-dict", "-zlzma,6,dictsize=3145728", 4096)]
    [InlineData("unicode-volume", "-zlz4 -L固件测试", 4096)]
    public async Task NativeImage_Edit_Rebuild_PreservesParametersAndPassesFsckAndMount(string name, string compression, int blockSize)
    {
        if (!WslTestHelpers.IsAlpineAvailable()) Assert.Skip("Alpine WSL2 is unavailable.");
        var available = WslTestHelpers.RunWsl($"test -x {Tool("mkfs.erofs")} && test -x {Tool("fsck.erofs")}");
        if (available.ExitCode != 0) Assert.Skip("EROFS native tools are unavailable; set UOTAN_EROFS_TOOLS.");
        var useFuse = name is "lzma" or "deflate" or "zstd" or "mixed" or "deflate-window" or "lzma-dict";
        if (useFuse && WslTestHelpers.RunWsl($"test -x {Tool("erofsfuse")}").ExitCode != 0)
            Assert.Skip("erofsfuse is unavailable.");
        if (!useFuse)
        {
            var kernel = KernelMountVerifier.Probe("erofs", compression.Length == 0 ? null : "CONFIG_EROFS_FS_ZIP");
            if (!kernel.Available) Assert.Skip(kernel.Detail);
        }
        if (name is "lzma" or "zstd" or "mixed")
        {
            var help = WslTestHelpers.RunWsl($"{Tool("mkfs.erofs")} --help");
            if (!help.Output.Contains(name == "mixed" ? "zstd" : name, StringComparison.Ordinal)) Assert.Skip($"Native EROFS tools lack {name}.");
        }
        // erofs-utils 1.9.4 has an init-order defect where --compress-hints dereferences the ZIP
        // config table before mkfs.erofs assigns importer_params.z_paramsets, crashing with SIGSEGV
        // for any compression combination. It is a tool bug, not a project one, so smoke-test the
        // exact mixed+hints command once and skip with the reason when it still faults. An explicit
        // UOTAN_EROFS_MIXED_MKFS override is trusted as already-patched and bypasses the probe.
        // erofs-utils 1.9.4 存在初始化顺序缺陷：--compress-hints 在 mkfs.erofs 给
        // importer_params.z_paramsets 赋值前解引用压缩配置表，任何压缩组合都会段错误崩溃。
        // 这是工具缺陷而非项目问题，故用与正式用例相同的 mixed+hints 命令冒烟一次，若仍崩溃则
        // 写明原因跳过；显式 UOTAN_EROFS_MIXED_MKFS 覆盖视为已修复，跳过探测。
        if (name == "mixed" && Environment.GetEnvironmentVariable("UOTAN_EROFS_MIXED_MKFS") is not { Length: > 0 })
        {
            var probeRoot = "/tmp/uotan-erofs-hint-probe";
            var probe = WslTestHelpers.RunWsl(
                $"rm -rf {probeRoot}\nmkdir -p {probeRoot}/src\nhead -c 2048 /dev/urandom > {probeRoot}/src/data\n" +
                $"printf '4096 0 (^|/)data$\\n' > {probeRoot}/hints\n" +
                $"{Tool("mkfs.erofs")} -b4096 --compress-hints={probeRoot}/hints -zlz4:lzma,6:deflate,6:zstd,3 {probeRoot}/smoke.img {probeRoot}/src\n" +
                $"code=$?\nrm -rf {probeRoot}\nexit \"$code\"\n", asRoot: true);
            if (probe.ExitCode != 0)
                Assert.Skip("erofs-utils 1.9.4 --compress-hints faults (SIGSEGV) before any pcluster is written; set UOTAN_EROFS_MIXED_MKFS to a fixed build.");
        }
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        using var host = new TempDirectory();
        var source = Path.Combine(host.Path, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        var originalData = string.Concat(Enumerable.Repeat("0123456789 abcdefghijklmnopqrstuvwxyz EROFS native rebuild\n", 10000));
        var changedData = originalData.Replace("native", "edited", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(source, "data"), originalData);
        File.WriteAllText(Path.Combine(source, "other"), originalData);
        File.WriteAllText(Path.Combine(source, "third"), originalData);
        File.WriteAllText(Path.Combine(source, "fourth"), originalData);
        File.WriteAllText(Path.Combine(source, "nested", "small"), "small tail\n");
        var random = new byte[33001];
        new Random(913).NextBytes(random);
        File.WriteAllBytes(Path.Combine(source, "random"), random);
        var moderate = Enumerable.Range(0, 32).SelectMany(_ => random.Take(8192)).ToArray();
        File.WriteAllBytes(Path.Combine(source, "moderate"), moderate);
        File.WriteAllBytes(Path.Combine(source, "empty"), []);
        var originalImage = Path.Combine(host.Path, "original.img");
        var rebuiltImage = Path.Combine(host.Path, "rebuilt.img");
        var linux = "/tmp/uotan-erofs-rebuild-" + Guid.NewGuid().ToString("N");
        var mkfs = name == "mixed" && Environment.GetEnvironmentVariable("UOTAN_EROFS_MIXED_MKFS") is { Length: > 0 } mixedMkfs
            ? WslTestHelpers.Quote(mixedMkfs) : Tool("mkfs.erofs");
        string Q(string path) => WslTestHelpers.Quote(WslTestHelpers.ToWslPath(path));
        var log = new System.Text.StringBuilder();
        void Run(string command, bool root = true)
        {
            var result = WslTestHelpers.RunWsl("set -eu\n" + command, 120000, asRoot: root);
            log.AppendLine(result.Output);
            Assert.True(result.ExitCode == 0, result.Output);
        }
        try
        {
            File.WriteAllText(Path.Combine(host.Path, "hints"), "4096 0 (^|/)(data|alias)$\n4096 1 (^|/)other$\n4096 2 (^|/)third$\n4096 3 (^|/)fourth$\n");
            Run($"mkdir -p {linux}/source\ncp -r {Q(source)}/. {linux}/source/\n" +
                $"cd {linux}/source\nln data alias\nln -s nested/small link\nmkfifo pipe\n" +
                "chmod 0750 data\nchown 70000:80000 data\n" +
                "setfattr -n user.rebuild -v preserved data\nsetfattr -n user.rebuild -v preserved other\n" +
                (name == "lz4-times" ? "touch -t 202311142213.20 other\npython3 -c 'import os; os.utime(\"data\", ns=(1650000000123456789,1650000000123456789))'\n" : "") +
                $"{mkfs} -b{blockSize} -T1700000000 -U00112233-4455-6677-8899-aabbccddeeff -Loriginal -x0 {compression} " +
                (name == "mixed" ? $"--compress-hints={Q(Path.Combine(host.Path, "hints"))} " : "") +
                $"{Q(originalImage)} {linux}/source\n" +
                $"{Tool("fsck.erofs")} --extract {Q(originalImage)}\n");
            var handler = new ErofsFormatHandler();
            var extracted = Path.Combine(host.Path, "extracted");
            var unpack = await handler.UnpackAsync(originalImage, extracted, cancellationToken: Token);
            Assert.True(unpack.Success, unpack.Summary);
            var restore = await handler.PackAsync(extracted, rebuiltImage, Token);
            Assert.True(restore.Success, restore.Summary);
            // The structured-metadata design rebuilds from the extracted tree, so the result is
            // content-equivalent rather than byte-identical to the native source image.
            // 结构化元数据设计从解包目录重建，因此结果与原生源镜像内容等价而非字节一致。
            using (var sourceParser = new ErofsParser(originalImage))
            using (var unmodifiedParser = new ErofsParser(rebuiltImage))
            {
                var sourceEntries = sourceParser.EnumerateEntries();
                var unmodifiedEntries = unmodifiedParser.EnumerateEntries();
                Assert.Equal(sourceEntries.Count, unmodifiedEntries.Count);
                foreach (var expectedEntry in sourceEntries)
                {
                    var actualEntry = Assert.Single(unmodifiedEntries, e => e.Path == expectedEntry.Path);
                    Assert.Equal(expectedEntry.Size, actualEntry.Size);
                    Assert.Equal(expectedEntry.Mode, actualEntry.Mode);
                    Assert.Equal(expectedEntry.Uid, actualEntry.Uid);
                    Assert.Equal(expectedEntry.Gid, actualEntry.Gid);
                    Assert.Equal(expectedEntry.MTime, actualEntry.MTime);
                    Assert.Equal(expectedEntry.MTimeNanoseconds, actualEntry.MTimeNanoseconds);
                    if (!expectedEntry.IsDirectory && !expectedEntry.IsSpecialFile)
                        Assert.Equal(sourceParser.ReadFileBytes(expectedEntry.Path), unmodifiedParser.ReadFileBytes(expectedEntry.Path));
                }
            }
            File.WriteAllText(Path.Combine(extracted, "data"), changedData);
            File.WriteAllText(Path.Combine(extracted, "added"), "new file\n");
            Directory.CreateDirectory(Path.Combine(extracted, "new-directory"));
            var rebuild = await handler.PackAsync(extracted, rebuiltImage, Token);
            Assert.True(rebuild.Success, rebuild.Summary);
            Assert.Contains("SourceFilesystemSize", rebuild.Metadata.Keys);
            Assert.Contains("RebuiltSize", rebuild.Metadata.Keys);
            Assert.Contains("CompressionInference", rebuild.Metadata.Keys);
            Assert.True(long.TryParse(rebuild.Metadata["RebuiltSize"], out var reportedSize));
            Assert.Equal(new FileInfo(rebuiltImage).Length, reportedSize);
            using var before = new ErofsParser(originalImage);
            using var after = new ErofsParser(rebuiltImage);
            Assert.Equal(before.Superblock.BlockSize, after.Superblock.BlockSize);
            Assert.Equal(before.SourceUuid, after.SourceUuid);
            Assert.Equal(before.SourceVolumeName, after.SourceVolumeName);
            Assert.Equal(before.Superblock.VolumeNameBytes, after.Superblock.VolumeNameBytes);
            Assert.Equal(before.Superblock.BuildTime, after.Superblock.BuildTime);
            Assert.Equal(before.Superblock.BuildTimeNsec, after.Superblock.BuildTimeNsec);
            Assert.Equal(before.Superblock.HasZtailpacking, after.Superblock.HasZtailpacking);
            var expectedProfile = before.ReadRebuildOptions();
            var actualProfile = after.ReadRebuildOptions();
            if (name == "mixed")
                Assert.Equal(4, expectedProfile.SourceInodes!.Values.Where(i => i.Compressed).Select(i => i.Algorithm).Distinct().Count());
            Assert.Equal(expectedProfile.Compress, actualProfile.Compress);
            Assert.Equal(expectedProfile.Algorithm, actualProfile.Algorithm);
            Assert.Equal(expectedProfile.DictionarySize, actualProfile.DictionarySize);
            Assert.Equal(expectedProfile.WindowBits, actualProfile.WindowBits);
            foreach (var expected in before.EnumerateEntries())
            {
                var actual = Assert.Single(after.EnumerateEntries(), e => e.Path == expected.Path);
                Assert.Equal(expected.Mode, actual.Mode);
                Assert.Equal(expected.Uid, actual.Uid);
                Assert.Equal(expected.Gid, actual.Gid);
                Assert.Equal(expected.MTime, actual.MTime);
                Assert.Equal(expected.MTimeNanoseconds, actual.MTimeNanoseconds);
                Assert.Equal(expected.Xattrs?.Select(x => (x.Name, Convert.ToHexString(x.Value))).Order(),
                    actual.Xattrs?.Select(x => (x.Name, Convert.ToHexString(x.Value))).Order());
                var expectedInode = expectedProfile.SourceInodes![expected.Nid];
                var actualInode = actualProfile.SourceInodes![actual.Nid];
                if (!expectedInode.Compressed || expected.Path is "data" or "alias" or "other" or "third" or "fourth")
                    Assert.Equal(expectedInode.Compressed, actualInode.Compressed);
                if (expectedInode.Compressed && actualInode.Compressed)
                {
                    Assert.Equal(expectedInode.Algorithm, actualInode.Algorithm);
                    Assert.Equal(expectedInode.CompactIndexes, actualInode.CompactIndexes);
                }
            }
            Assert.Equal(changedData, System.Text.Encoding.UTF8.GetString(after.ReadFileBytes("data")));
            Assert.Equal(after.ReadFileBytes("data"), after.ReadFileBytes("alias"));
            var hash = Hash(rebuiltImage);
            var expectedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["data"] = System.Text.Encoding.UTF8.GetBytes(changedData),
                ["alias"] = System.Text.Encoding.UTF8.GetBytes(changedData),
                ["other"] = System.Text.Encoding.UTF8.GetBytes(originalData),
                ["third"] = System.Text.Encoding.UTF8.GetBytes(originalData),
                ["fourth"] = System.Text.Encoding.UTF8.GetBytes(originalData),
                ["random"] = random,
                ["moderate"] = moderate,
                ["empty"] = [],
                ["nested/small"] = "small tail\n"u8.ToArray(),
                ["added"] = "new file\n"u8.ToArray()
            };
            var sums = Path.Combine(host.Path, "SHA256SUMS");
            File.WriteAllText(sums, string.Concat(expectedFiles.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{Convert.ToHexStringLower(SHA256.HashData(p.Value))}  {p.Key}\n")));
            Run($"{Tool("fsck.erofs")} --extract={linux}/checked --xattrs {Q(rebuiltImage)}\n" +
                $"mkdir {linux}/mount\n" +
                (useFuse ? $"{Tool("erofsfuse")} -o ro {Q(rebuiltImage)} {linux}/mount\n" :
                    $"mount -t erofs -o loop,ro,nodev,nosuid {Q(rebuiltImage)} {linux}/mount\n") +
                $"trap 'umount {linux}/mount' EXIT\n" +
                $"(cd {linux}/checked && sha256sum -c {Q(sums)})\n" +
                $"(cd {linux}/mount && sha256sum -c {Q(sums)})\n" +
                $"cmp {linux}/checked/data {linux}/mount/data\n" +
                $"cmp {Q(Path.Combine(extracted, "data"))} {linux}/mount/data\n" +
                $"test $(stat -c %i {linux}/mount/data) = $(stat -c %i {linux}/mount/alias)\n" +
                $"test $(stat -c %u:%g:%a {linux}/mount/data) = 70000:80000:750\n" +
                $"test $(stat -c %Y {linux}/mount/other) = 1700000000\n" +
                $"test $(readlink {linux}/mount/link) = nested/small\n" +
                $"test -p {linux}/mount/pipe\n" +
                $"test -d {linux}/mount/nested/empty\ntest -d {linux}/mount/new-directory\n" +
                $"getfattr -n user.rebuild --only-values {linux}/mount/data\n" +
                $"cat /proc/mounts | grep {linux}/mount\n" +
                $"echo VERIFIED_{name}\n");
            Assert.Equal(hash, Hash(rebuiltImage));
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("UOTAN_EROFS_ARTIFACTS") is { Length: > 0 } artifacts)
            {
                var target = Path.Combine(artifacts, name);
                Directory.CreateDirectory(target);
                foreach (var file in new[] { originalImage, rebuiltImage })
                    if (File.Exists(file)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
                File.WriteAllText(Path.Combine(target, "native.log"), log.ToString());
            }
            WslTestHelpers.RunWsl($"umount {linux}/mount 2>/dev/null || true\nrm -rf {linux}", asRoot: true);
        }
    }

    [Fact]
    public async Task CorpusImage_Edit_PassesNativeFsckAndKernelComparison()
    {
        var original = UotanStudio.Tests.Infrastructure.FirmwareCorpus.FindFile("odm.img");
        if (original is null) Assert.Skip("The EROFS odm.img corpus is unavailable.");
        var capability = KernelMountVerifier.Probe("erofs", "CONFIG_EROFS_FS_ZIP");
        if (!capability.Available) Assert.Skip(capability.Detail);
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        using var host = new TempDirectory();
        using var before = new ErofsParser(original);
        var file = before.EnumerateFiles().Where(e => !e.IsSymlink && !e.IsSpecialFile && e.Size is > 0 and < 65536)
            .OrderByDescending(e => e.Path.EndsWith(".prop", StringComparison.Ordinal)).ThenBy(e => e.Path, StringComparer.Ordinal).First();
        var handler = new ErofsFormatHandler();
        var extracted = Path.Combine(host.Path, "extracted");
        var result = await handler.UnpackAsync(original, extracted, cancellationToken: Token);
        Assert.True(result.Success, result.Summary);
        var edited = Path.Combine(extracted, file.Path.Replace('/', Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(edited, "\n# EROFS rebuild validation\n", Token);
        var rebuilt = Path.Combine(host.Path, "rebuilt.img");
        result = await handler.PackAsync(extracted, rebuilt, Token);
        Assert.True(result.Success, result.Summary);
        var linux = "/tmp/uotan-erofs-corpus-" + Guid.NewGuid().ToString("N");
        var originalHash = Hash(original);
        var rebuiltHash = Hash(rebuilt);
        var compare = "python3 - " + WslTestHelpers.Quote(linux + "/before") + " " + WslTestHelpers.Quote(linux + "/after") + " " +
            WslTestHelpers.Quote(file.Path) + " " + WslTestHelpers.Quote(Hash(edited).ToLowerInvariant()) + " <<'PY'\n" +
            "import os,sys,stat,hashlib\n" +
            "def snapshot(root):\n" +
            "    result={}\n    pending=['.']\n    links={}\n" +
            "    while pending:\n        rel=pending.pop()\n        path=os.path.join(root,rel)\n        s=os.lstat(path)\n" +
            "        attrs={k:os.getxattr(path,k,follow_symlinks=False).hex() for k in os.listxattr(path,follow_symlinks=False)}\n" +
            "        row=[s.st_mode,s.st_uid,s.st_gid,s.st_mtime_ns,s.st_rdev,attrs,None]\n" +
            "        if stat.S_ISDIR(s.st_mode): pending.extend(os.path.join(rel,n) for n in os.listdir(path))\n" +
            "        elif stat.S_ISLNK(s.st_mode): row[-1]=os.readlink(path)\n" +
            "        elif stat.S_ISREG(s.st_mode):\n            with open(path,'rb') as f: row[-1]=hashlib.file_digest(f,'sha256').hexdigest()\n            links.setdefault(s.st_ino,[]).append(rel)\n" +
            "        result[rel]=row\n    return result,sorted(sorted(g) for g in links.values() if len(g)>1)\n" +
            "a,al=snapshot(sys.argv[1]); b,bl=snapshot(sys.argv[2]); edited='./'+sys.argv[3]\n" +
            "assert a.keys()==b.keys(), 'Directory tree mismatch'\n" +
            "a[edited][-1]=sys.argv[4]\n" +
            "for group in al:\n    if edited in group:\n        for alias in group: a[alias][-1]=sys.argv[4]\n" +
            "assert al==bl, 'Hardlink group mismatch'\n" +
            "for p in a: assert a[p]==b[p], (p,a[p],b[p])\n" +
            "print('CORPUS_KERNEL_VERIFIED',len(a),'entries',len(al),'hardlink groups')\nPY\n";
        var output = WslTestHelpers.RunWsl("set -eu\n" +
            $"{Tool("fsck.erofs")} --extract {WslTestHelpers.Quote(WslTestHelpers.ToWslPath(rebuilt))}\n" +
            $"mkdir -p {linux}/before {linux}/after\n" +
            $"trap 'umount {linux}/after 2>/dev/null || true; umount {linux}/before 2>/dev/null || true; rmdir {linux}/after {linux}/before {linux}' EXIT\n" +
            $"mount -t erofs -o loop,ro,nodev,nosuid {WslTestHelpers.Quote(WslTestHelpers.ToWslPath(original))} {linux}/before\n" +
            $"mount -t erofs -o loop,ro,nodev,nosuid {WslTestHelpers.Quote(WslTestHelpers.ToWslPath(rebuilt))} {linux}/after\n" + compare,
            180000, asRoot: true);
        if (Environment.GetEnvironmentVariable("UOTAN_EROFS_ARTIFACTS") is { Length: > 0 } artifacts)
        {
            var target = Path.Combine(artifacts, "corpus-odm");
            Directory.CreateDirectory(target);
            File.Copy(rebuilt, Path.Combine(target, "rebuilt.img"), true);
            File.WriteAllText(Path.Combine(target, "native.log"), output.Output + $"\nOriginal: {original}\nSHA256: {originalHash}\nRebuilt SHA256: {rebuiltHash}\nEdited: {file.Path}\n");
        }
        Assert.True(output.ExitCode == 0, output.Output);
        Assert.Equal(originalHash, Hash(original));
        Assert.Equal(rebuiltHash, Hash(rebuilt));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}