using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UotanStudio.Services.Delivery;

/// <summary>Generates scripts from one validated plan; never executes device commands.</summary>
internal static partial class DeliveryScriptGenerator
{
    internal static string Fingerprint(DeliveryPlan plan) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(plan with { Scripts = [], GeneratedScripts = [], OpenScripts = [], ScriptCarets = [], ScriptFingerprint = "", OutputDirectory = "" },
            DeliveryJsonContext.Default.DeliveryPlan)));

    internal static Dictionary<string, string> Generate(DeliveryPlan plan)
    {
        DeliveryValidation.Validate(plan, false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!plan.OrganizeDirectory) return result;
        var flashFiles = plan.Files.Where(f => !f.FileName.Equals("payload.bin", StringComparison.OrdinalIgnoreCase) && f.Partition != "payload").ToList();
        if (flashFiles.Count == 0) return result;
        plan = plan with { Files = flashFiles };
        var groups = plan.Files.GroupBy(f => f.Partition, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        if (groups.Any(g => g.Length > 9)) throw new InvalidDataException("每个选择组最多 9 项 / At most 9 alternatives per partition.");
        var defaults = groups.Select(g => g.FirstOrDefault(f => f.Default) ?? g[0]).Where(f => !f.Optional || f.Default).ToArray();
        if (plan.Txt)
        {
            result.Add("flash.txt", Txt(defaults));
            if (plan.TxtVariants)
                foreach (var group in groups)
                    foreach (var alternative in group.Where(f => f.Optional && !f.Default))
                    {
                        var variant = groups.Select(g => g[0].Partition == alternative.Partition ? alternative
                            : g.FirstOrDefault(f => f.Default) ?? g[0]).Where(f => f == alternative || !f.Optional || f.Default);
                        result.Add("flash-" + Path.GetFileNameWithoutExtension(alternative.FileName) + ".txt", Txt(variant));
                    }
        }
        if (plan.Recovery && !plan.PackPayload) result.Add("Recovery.sh", DeliveryPackageBuilder.RecoveryScript(plan));
        if (plan.Windows) result.Add("FlashWindows.bat", Batch(plan, groups));
        if (plan.Linux) result.Add("FlashLinux.sh", Shell(plan, groups, "Linux"));
        if (plan.MacOS) result.Add("FlashMacOS.sh", Shell(plan, groups, "macOS"));
        return result;
    }

    private static string Txt(IEnumerable<DeliveryFile> files) => string.Concat(files.Select(f => $"{f.Partition} images/{f.FileName}\r\n"));
    private static string Sh(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static string Shell(DeliveryPlan plan, DeliveryFile[][] groups, string platform)
    {
        var b = new StringBuilder("#!/bin/bash\nset -eu\nROOT=\"$(cd -- \"$(dirname -- \"$0\")\" && pwd)\"\n");
        b.AppendLine($"TOOLS=\"$ROOT/bin/{platform}\"");
        b.AppendLine(ShellIntegrity(platform, plan));
        b.AppendLine("die() { printf '%s\\n' \"$*\" >&2; exit 1; }");
        b.AppendLine("[ -x \"$TOOLS/fastboot\" ] || die 'fastboot missing or not executable; chmod +x bin/*/fastboot'");
        b.AppendLine("WORK=$(mktemp -d)\ntrap 'rm -rf -- \"$WORK\"' EXIT");
        b.AppendLine("count=$(\"$TOOLS/fastboot\" devices | awk 'NF {n++} END {print n+0}')\n[ \"$count\" = 1 ] || die 'Connect exactly one Fastboot device.'");
        if (plan.DeviceCodename.Length > 0)
        {
            b.AppendLine("product=$(\"$TOOLS/fastboot\" getvar product 2>&1) || die 'Unable to read device product.'");
            b.AppendLine($"printf '%s\\n' \"$product\" | tr -d '\\r' | grep -Eq {Sh("^(\\(bootloader\\) )?product: *" + plan.DeviceCodename.Replace(".", @"\.") + "$")} || die 'Device mismatch.'");
        }
        foreach (var f in plan.Files) b.AppendLine($"[ -f \"$ROOT/images/{f.FileName}\" ] || die {Sh("Missing image: " + f.FileName)}");
        if (plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
            b.AppendLine("[ -x \"$TOOLS/zstd\" ] || die 'zstd missing or not executable.'");
        foreach (var line in plan.Information.Replace("\r", "").Split('\n')) b.AppendLine($"printf '%s\\n' {Sh(line)}");
        b.AppendLine("flash() {\n  local target=\"$1\" name=\"$2\" file=\"$ROOT/images/$2\"\n  case \"$name\" in\n    *.zst) file=\"$WORK/${name%.zst}.img\"; \"$TOOLS/zstd\" -d -o \"$file\" -- \"$ROOT/images/$name\" || die 'Decompression failed.' ;;\n  esac");
        b.AppendLine("  if [ \"$target\" = super_empty ]; then\n    \"$TOOLS/fastboot\" wipe-super \"$file\" || die 'wipe-super failed.'");
        b.AppendLine("  elif [ \"$target\" = super ]; then\n    \"$TOOLS/fastboot\" flash \"$target\" \"$file\" || die 'Super flash failed.'\n  else");
        b.AppendLine($"    \"$TOOLS/fastboot\" {(plan.Slot.Length > 0 ? "--slot=" + plan.Slot + " " : "")}flash \"$target\" \"$file\" || die 'Flash failed.'\n  fi\n}}");
        b.AppendLine(ShellMode());
        // Collect every choice before the first write, so invalid input cannot partially flash a plan.
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var optional = group.Length == 1 && group[0].Optional;
            var defaultIndex = optional && !group[0].Default ? 0 : Array.FindIndex(group, f => f.Default) + 1;
            if (defaultIndex < 1 && !optional) defaultIndex = 1;
            if (group.Length == 1 && !optional) { b.AppendLine($"choice{i}=1"); continue; }
            b.AppendLine($"printf '%s\\n' {Sh("Choose " + group[0].Partition)}");
            if (optional) b.AppendLine("printf '%s\\n' '0: Skip'");
            for (var j = 0; j < group.Length; j++) b.AppendLine($"printf '%s\\n' {Sh($"{j + 1}: {group[j].FileName}")}");
            var valid = string.Join('|', Enumerable.Range(optional ? 0 : 1, group.Length + (optional ? 1 : 0)));
            b.AppendLine($"while :; do\n  printf '%s' 'Choice [{defaultIndex}]: '\n  IFS= read -r answer || die 'Input cancelled.'\n  answer=${{answer:-{defaultIndex}}}\n  case \"$answer\" in {valid}) choice{i}=\"$answer\"; break ;; *) printf '%s\\n' 'Invalid choice.' ;; esac\ndone");
        }
        if (plan.AskWipe) b.AppendLine("printf '%s' 'Erase userdata and metadata? [y/N]: '\nIFS= read -r wipe || die 'Input cancelled.'");
        b.AppendLine("printf '%s' 'Start flashing? [y/N]: '\nIFS= read -r confirm || die 'Input cancelled.'\ncase \"$confirm\" in y|Y) ;; *) exit 0 ;; esac");
        foreach (var i in Enumerable.Range(0, groups.Length).OrderBy(i => groups[i].Any(NeedsFastbootd)))
        {
            b.AppendLine($"case \"$choice{i}\" in");
            for (var j = 0; j < groups[i].Length; j++) b.AppendLine($"  {j + 1}) ensure_mode {(NeedsFastbootd(groups[i][j]) ? "yes" : "no")}; flash {Sh(groups[i][j].Partition)} {Sh(groups[i][j].FileName)} ;;");
            b.AppendLine("esac");
        }
        if (plan.AskWipe) b.AppendLine("case \"$wipe\" in y|Y) \"$TOOLS/fastboot\" erase metadata && \"$TOOLS/fastboot\" erase userdata || die 'Erase failed.' ;; esac");
        if (plan.Reboot) b.AppendLine("\"$TOOLS/fastboot\" reboot || die 'Reboot failed.'");
        b.AppendLine("printf '%s\\n' 'Completed.'");
        return b.ToString().Replace("\r\n", "\n");
    }

    private static string Batch(DeliveryPlan plan, DeliveryFile[][] groups)
    {
        var b = new StringBuilder("@echo off\r\nsetlocal DisableDelayedExpansion\r\nset \"WORK=\"\r\nchcp 65001 >nul\r\nset \"ROOT=%~dp0\"\r\nset \"TOOLS=%~dp0bin\\Windows\"\r\n");
        b.AppendLine(BatchIntegrity(plan));
        b.AppendLine("if not exist \"%TOOLS%\\fastboot.exe\" goto failed\nset \"WORK=%TEMP%\\uotan-flash-%RANDOM%-%RANDOM%\"\nif exist \"%WORK%\" exit /b 1\nmkdir \"%WORK%\" || exit /b 1");
        b.AppendLine("\"%TOOLS%\\fastboot.exe\" devices >\"%WORK%\\devices.txt\" 2>&1\nif errorlevel 1 goto failed\nset COUNT=0\nfor /f \"usebackq tokens=1\" %%D in (\"%WORK%\\devices.txt\") do set /a COUNT+=1 >nul\nif not \"%COUNT%\"==\"1\" goto failed");
        if (plan.DeviceCodename.Length > 0)
        {
            b.AppendLine("\"%TOOLS%\\fastboot.exe\" getvar product >\"%WORK%\\product.txt\" 2>&1\nif errorlevel 1 goto failed");
            b.AppendLine($"findstr /r /x /c:\"product: *{plan.DeviceCodename.Replace(".", @"\.")}\" /c:\"(bootloader) product: *{plan.DeviceCodename.Replace(".", @"\.")}\" \"%WORK%\\product.txt\" >nul\nif errorlevel 1 goto failed");
        }
        foreach (var f in plan.Files) b.AppendLine($"if not exist \"%ROOT%images\\{f.FileName}\" goto failed");
        if (plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
            b.AppendLine("if not exist \"%TOOLS%\\zstd.exe\" goto failed");
        foreach (var line in plan.Information.Replace("\r", "").Split('\n'))
            b.AppendLine("echo(" + line.Replace("^", "^^").Replace("%", "%%").Replace("&", "^&").Replace("|", "^|")
                .Replace("<", "^<").Replace(">", "^>").Replace("(", "^(").Replace(")", "^)"));
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var optional = group.Length == 1 && group[0].Optional;
            if (group.Length == 1 && !optional) { b.AppendLine($"set CHOICE{i}=1"); continue; }
            b.AppendLine($"echo Choose {group[0].Partition}");
            if (optional) b.AppendLine("echo 0: Skip");
            for (var j = 0; j < group.Length; j++) b.AppendLine($"echo {j + 1}: {group[j].FileName}");
            var keys = string.Concat(Enumerable.Range(optional ? 0 : 1, group.Length + (optional ? 1 : 0)));
            b.AppendLine($"choice /c {keys} /n /m \"Choice: \"\nif errorlevel 255 goto failed\nif errorlevel 1 (goto chosen{i}) else (goto cancelled)\n:chosen{i}\nset CHOICE{i}=%ERRORLEVEL%");
            if (optional) b.AppendLine($"set /a CHOICE{i}-=1 >nul");
        }
        if (plan.AskWipe) b.AppendLine("choice /c NY /n /m \"Erase userdata and metadata? [N/Y]: \"\nif errorlevel 255 goto failed\nif errorlevel 1 (set WIPE=%ERRORLEVEL%) else (goto cancelled)");
        b.AppendLine("choice /c NY /n /m \"Start flashing? [N/Y]: \"\nif errorlevel 255 goto failed\nif not errorlevel 2 goto cancelled");
        foreach (var i in Enumerable.Range(0, groups.Length).OrderBy(i => groups[i].Any(NeedsFastbootd)))
            for (var j = 0; j < groups[i].Length; j++)
            {
                b.AppendLine($"if not \"%CHOICE{i}%\"==\"{j + 1}\" goto skip{i}_{j}\ncall :ensure_mode {(NeedsFastbootd(groups[i][j]) ? "yes" : "no")}\nif errorlevel 1 goto failed\ncall :flash {groups[i][j].Partition} {groups[i][j].FileName}\nif errorlevel 1 goto failed\n:skip{i}_{j}");
            }
        if (plan.AskWipe) b.AppendLine("if not \"%WIPE%\"==\"2\" goto no_wipe\n\"%TOOLS%\\fastboot.exe\" erase metadata\nif errorlevel 1 goto failed\n\"%TOOLS%\\fastboot.exe\" erase userdata\nif errorlevel 1 goto failed\n:no_wipe");
        if (plan.Reboot) b.AppendLine("\"%TOOLS%\\fastboot.exe\" reboot\nif errorlevel 1 goto failed");
        b.AppendLine("echo Completed.\n:cancelled\nif defined WORK rmdir /s /q \"%WORK%\"\nexit /b 0\n:failed\necho Delivery failed. Check device, images and tools.\nif defined WORK if exist \"%WORK%\" rmdir /s /q \"%WORK%\"\nexit /b 1");
        b.AppendLine(":flash\nset \"IMAGE=%ROOT%images\\%~2\"\nif /i not \"%~x2\"==\".zst\" goto flash_ready\nset \"IMAGE=%WORK%\\%~n2.img\"\n\"%TOOLS%\\zstd.exe\" -d \"%ROOT%images\\%~2\" -o \"%IMAGE%\"\nif errorlevel 1 exit /b 1\n:flash_ready\nif \"%~1\"==\"super_empty\" goto wipe_super\nif \"%~1\"==\"super\" goto flash_super");
        b.AppendLine($"\"%TOOLS%\\fastboot.exe\" {(plan.Slot.Length > 0 ? "--slot=" + plan.Slot + " " : "")}flash %~1 \"%IMAGE%\"\nexit /b %ERRORLEVEL%\n:flash_super\n\"%TOOLS%\\fastboot.exe\" flash super \"%IMAGE%\"\nexit /b %ERRORLEVEL%\n:wipe_super\n\"%TOOLS%\\fastboot.exe\" wipe-super \"%IMAGE%\"\nexit /b %ERRORLEVEL%");
        b.AppendLine(BatchMode());
        return b.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
