using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Tests.Firmware;

/// <summary>Runs read-only mount checks without installing or configuring the distribution. <para>不安装或修改发行版配置，执行只读挂载检查。</para></summary>
internal static class KernelMountVerifier
{
    internal static (bool Available, string Detail) Probe(string format, string? requiredConfiguration)
    {
        if (!WslTestHelpers.IsAlpineAvailable())
            return (false, $"WSL distribution '{WslTestHelpers.DistroName}' is unavailable.");
        var kernel = Run("uname -r");
        if (kernel.ExitCode != 0 || !kernel.Output.Contains("WSL2", StringComparison.OrdinalIgnoreCase))
            return (false, "An available WSL2 kernel is required. " + kernel.Output);
        var probe = "set -eu\n" +
            "unavailable() { echo \"$*\"; exit 77; }\n" +
            "for tool in mount umount mktemp rmdir find sort diff sha256sum grep cat id sed; do command -v \"$tool\" >/dev/null || unavailable \"Missing host tool: $tool\"; done\n" +
            "[ \"$(id -u)\" = 0 ] || unavailable 'Root access is unavailable.'\n" +
            "caps=$(sed -n 's/^CapEff:[[:space:]]*//p' /proc/self/status)\n" +
            "{ [ -n \"$caps\" ] && [ $((0x$caps & 0x00200000)) -ne 0 ]; } || unavailable 'CAP_SYS_ADMIN is unavailable.'\n" +
            $"grep -qw {Quote(format)} /proc/filesystems || unavailable 'The {format} host driver is not loaded.'\n" +
            "grep -qw loop /proc/devices || unavailable 'The host loop driver is not loaded.'\n" +
            "test -c /dev/loop-control || unavailable '/dev/loop-control is unavailable.'\n";
        if (requiredConfiguration is not null)
            probe += $"zcat /proc/config.gz | grep -q '^{requiredConfiguration}=y$' || unavailable 'Kernel capability {requiredConfiguration} is unavailable or cannot be inspected.'\n";
        var result = Run(probe);
        return result.ExitCode == 0
            ? (true, kernel.Output.Trim())
            : (false, result.Output.Trim());
    }

    internal static (bool Ok, string Output) Verify(string image, string source, string format)
    {
        var reference = Path.Combine(Path.GetTempPath(), "uotan-mount-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reference);
        try
        {
            string Relative(string path) => "./" + Path.GetRelativePath(source, path).Replace('\\', '/');
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(Relative, StringComparer.Ordinal).ToArray();
            var hashes = new List<string>();
            foreach (var file in files)
            {
                using var input = File.OpenRead(file);
                hashes.Add($"{Convert.ToHexStringLower(SHA256.HashData(input))}  {Relative(file)}");
            }
            void WriteLines(string path, IEnumerable<string> lines) => File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
            WriteLines(Path.Combine(reference, "SHA256SUMS"), hashes);
            WriteLines(Path.Combine(reference, "files.txt"), files.Select(Relative));
            WriteLines(Path.Combine(reference, "directories.txt"), new[] { "." }.Concat(Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Select(Relative)).Order(StringComparer.Ordinal));
            var options = format switch { "ext4" => "loop,ro,noload", "f2fs" => "loop,ro,norecovery", _ => "loop,ro" };
            var expected = Quote(WslTestHelpers.ToWslPath(reference));
            var script = "set -eu\nexport LC_ALL=C\nmp=$(mktemp -d /tmp/uotan-kernel-mount.XXXXXX)\nmounted=0\n" +
                "trap 'status=$?; if [ \"$mounted\" = 1 ]; then umount \"$mp\" || status=1; fi; rmdir \"$mp\"; exit \"$status\"' EXIT\n" +
                $"mount -t {Quote(format)} -o {Quote(options + ",nosuid,nodev")} {Quote(WslTestHelpers.ToWslPath(image))} \"$mp\"\nmounted=1\n" +
                "cat /proc/mounts | grep \" $mp \"\n" +
                $"(cd \"$mp\" && find . -type f | sort) | diff - {expected}/files.txt\n" +
                $"(cd \"$mp\" && find . -type d | sort) | diff - {expected}/directories.txt\n" +
                $"(cd \"$mp\" && sha256sum -c {expected}/SHA256SUMS)\n" +
                "umount \"$mp\"\nmounted=0\necho MOUNT_VERIFY_OK\n";
            var result = Run(script);
            return (result.ExitCode == 0 && result.Output.Contains("MOUNT_VERIFY_OK", StringComparison.Ordinal), result.Output);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(reference)) File.Delete(file);
            Directory.Delete(reference);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static (string Output, int ExitCode) Run(string command)
        => WslTestHelpers.RunWsl(command, 60000, asRoot: true);
}