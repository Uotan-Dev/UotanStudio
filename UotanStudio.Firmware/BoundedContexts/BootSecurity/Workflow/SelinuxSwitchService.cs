using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Workflow;

public enum SelinuxSwitchMode { Permissive, Enforcing, Disable }

/// <summary>Produces an auditable SELinux command-line switch plan and optionally applies it to boot.</summary>
public sealed class SelinuxSwitchService
{
    public SelinuxSwitchPlan CreatePlan(string bootPath, SelinuxSwitchMode mode)
    {
        using var parser = new BootImageParser();
        parser.ParseFile(bootPath);
        var original = parser.Header.CommandLine ?? string.Empty;
        var tokens = original.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.StartsWith("androidboot.selinux=", StringComparison.OrdinalIgnoreCase)
                     && !x.Equals("selinux=0", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mode != SelinuxSwitchMode.Disable)
            tokens.Add($"androidboot.selinux={(mode == SelinuxSwitchMode.Permissive ? "permissive" : "enforcing")}");
        var updated = string.Join(' ', tokens);
        return new SelinuxSwitchPlan(bootPath, mode, original, updated,
            ["boot header command line only", "SELinux policy binaries and file_contexts are unchanged", "real-device behavior requires separate validation"]);
    }

    public async Task ApplyAsync(SelinuxSwitchPlan plan, string outputPath, CancellationToken cancellationToken = default)
    {
        using var parser = new BootImageParser();
        parser.ParseFile(plan.BootPath);
        parser.Header.CommandLine = plan.UpdatedCommandLine;
        var bytes = BootImagePacker.Repack(parser);
        var temp = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, outputPath, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}

public sealed record SelinuxSwitchPlan(string BootPath, SelinuxSwitchMode Mode, string OriginalCommandLine,
    string UpdatedCommandLine, IReadOnlyList<string> Limitations);