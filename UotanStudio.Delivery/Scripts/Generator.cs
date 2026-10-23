using System.Text;
using System.Text.Json;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Delivery;

/// <summary><para>根据已验证的计划生成脚本，绝不执行设备命令。</para>
/// Generates scripts from one validated plan; never executes device commands.</summary>
public static partial class DeliveryScriptGenerator
{
    private static string ImagePath(DeliveryPlan plan, DeliveryFile file)
        => plan.PackSuper ? DeliveryOutputService.OrganizedImageRelativePath(file).Replace('\\', '/') : "images/" + file.FileName;
    /// <summary>
    /// Computes a fingerprint over the plan's key fields to detect when generated scripts are stale relative to the installer contract.
    /// <para>对计划的关键字段计算脚本指纹，用于检测生成脚本是否随安装器契约或计划内容而过期。</para>
    /// </summary>
    public static string Fingerprint(DeliveryPlan plan)
    {
        using var hash = Sha256.CreateAccumulator();
        hash.Append("UotanStudio delivery scripts v3\n"u8);
        hash.Append(JsonSerializer.SerializeToUtf8Bytes(plan with { Scripts = [], GeneratedScripts = [], OpenScripts = [], ScriptCarets = [], ScriptFingerprint = "", OutputDirectory = "" },
            DeliveryJsonContext.Default.DeliveryPlan));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary><para>按计划生成脚本；localize 非空时用其解析用户可见提示文案，否则回退英文。</para>
    /// Generates scripts from the plan; when localize is set its lookup resolves user-visible prompts, otherwise English falls back.
    /// <para>excludeOatArtifacts 为可选开关：置位时按共享文件编排结果剔除 oat/ART 编译产物条目（.oat、.odex、.vdex），
    /// 默认关闭且不影响任何既有输出。</para>
    /// <para>excludeOatArtifacts is an opt-in switch: when set, the shared file-orchestration result drops
    /// oat/ART compiled-artifact entries (.oat, .odex, .vdex); it defaults to off and never changes existing outputs.</para></summary>
    public static Dictionary<string, string> Generate(DeliveryPlan plan, Func<string, string>? localize = null,
        bool excludeOatArtifacts = false)
    {
        DeliveryValidation.Validate(plan, false);
        if (plan.IsSuperEmptyOnly) return [];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!plan.OrganizeDirectory) return result;
        var flashFiles = plan.Files.Where(f => !f.FileName.Equals("payload.bin", StringComparison.OrdinalIgnoreCase) && f.Partition != "payload"
            && f.Partition != "super_empty"
            && !DeliveryPackageBuilder.IsDatArtifact(f.FileName)
            && !(excludeOatArtifacts && IsOatArtifact(f.FileName)))
            .Select(f => f.Generated && f.Partition == "super"
                ? f with { Generated = false, FileName = DeliveryOutputService.SuperFileName(plan.Super) }
                : f)
            .ToList();
        if (plan.Recovery && !plan.PackPayload)
        {
            if (DeliveryPackageBuilder.HasDat(plan))
            {
                result.Add("updater-script", DeliveryPackageBuilder.RecoveryUpdaterScript(plan, localize));
            }
            else
            {
                result.Add("update-binary", DeliveryPackageBuilder.RecoveryScript(plan, localize));
                result.Add("updater-script", DeliveryPackageBuilder.RecoveryUpdaterScript(plan, localize));
            }
        }
        if (flashFiles.Count == 0) return result;
        plan = plan with { Files = flashFiles };
        var groups = plan.Files.GroupBy(f => f.Partition, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        if (groups.Any(g => g.Length > 9)) throw new InvalidDataException("At most 9 alternatives per partition.");
        var defaults = groups.Select(g => g.FirstOrDefault(f => f.Default) ?? g[0]).Where(f => !f.Optional || f.Default).ToArray();
        if (plan.Txt && !plan.Super.Zstd)
        {
            static bool IsZstd(DeliveryFile f) => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
            result.Add("flash.txt", Txt(plan, defaults.Where(f => !IsZstd(f))));
            if (plan.TxtVariants)
                foreach (var group in groups)
                    foreach (var alternative in group.Where(f => f.Optional && !f.Default && !IsZstd(f)))
                    {
                        var variant = groups.Select(g => g[0].Partition == alternative.Partition ? alternative
                            : g.FirstOrDefault(f => f.Default) ?? g[0]).Where(f => f == alternative || !f.Optional || f.Default)
                            .Where(f => !IsZstd(f)).ToArray();
                        if (variant.Length > 0)
                            result.Add("flash-" + Path.GetFileNameWithoutExtension(alternative.FileName) + ".txt", Txt(plan, variant));
                    }
        }
        if (plan.Windows) result.Add("FlashWindows.bat", Batch(plan, groups, localize));
        if (plan.Linux) result.Add("FlashLinux.sh", Shell(plan, groups, "Linux", localize));
        if (plan.MacOS) result.Add("FlashMacOS.sh", Shell(plan, groups, "macOS", localize));
        return result;
    }

    /// <summary><para>解析本地化提示：localize 为空、返回键名本身或返回完整资源键名时使用英文回退。</para>
    /// Resolves a localized prompt; falls back to English when localize is null or returns the short key or the full resource key name.</summary>
    private static string T(Func<string, string>? localize, string key, string fallback, params ReadOnlySpan<object> args)
    {
        var text = localize?.Invoke(key) ?? fallback;
        if (text == key || text == "Delivery_Script_" + key) text = fallback;
        if (args.Length == 0) return text;
        try { return string.Format(text, args); }
        catch (FormatException) { return fallback; }
    }

    private static string Txt(DeliveryPlan plan, IEnumerable<DeliveryFile> files) => string.Concat(files.Select(f => $"{f.Partition} {ImagePath(plan, f)}\r\n"));
    private static string Sh(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    /// <summary><para>判断文件名是否为 oat/ART 编译产物（.oat、.odex、.vdex），供可选清理剔除共享文件编排条目。</para>
    /// Recognizes oat/ART compiled artifacts (.oat, .odex, .vdex) so the opt-in cleanup can drop them from the shared file-orchestration result.</summary>
    private static bool IsOatArtifact(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".oat" or ".odex" or ".vdex";

    private static string Shell(DeliveryPlan plan, DeliveryFile[][] groups, string platform, Func<string, string>? localize)
    {
        var b = new StringBuilder("#!/bin/bash\nset -eu\nROOT=\"$(cd -- \"$(dirname -- \"$0\")\" && pwd)\"\n");
        foreach (var line in BannerLines(plan, localize)) b.AppendLine($"printf '%s\\n' {Sh(line)}");
        foreach (var line in PrerequisiteLines(localize, false)) b.AppendLine($"printf '%s\\n' {Sh(line)}");
        b.AppendLine($"TOOLS=\"$ROOT/bin/{platform}\"");
        b.AppendLine(ShellIntegrity(platform, plan, localize));
        b.AppendLine("die() { printf '%s\\n' \"$*\" >&2; exit 1; }");
        b.AppendLine($"[ -x \"$TOOLS/fastboot\" ] || die {Sh(T(localize, "FastbootMissing", "fastboot missing or not executable; chmod +x bin/*/fastboot"))}");
        b.AppendLine("WORK=$(mktemp -d)\ntrap 'rm -rf -- \"$WORK\"' EXIT");
        b.AppendLine($"count=$(\"$TOOLS/fastboot\" devices | awk 'NF {{n++}} END {{print n+0}}')\n[ \"$count\" = 1 ] || die {Sh(T(localize, "ConnectOneDevice", "Connect exactly one Fastboot device."))}");
        if (plan.DeviceCodename.Length > 0)
        {
            b.AppendLine($"product=$(\"$TOOLS/fastboot\" getvar product 2>&1) || die {Sh(T(localize, "ReadProduct", "Unable to read device product."))}");
            b.AppendLine($"printf '%s\\n' \"$product\" | tr -d '\\r' | grep -Eq {Sh("^(\\(bootloader\\) )?product: *" + plan.DeviceCodename.Replace(".", @"\.") + "$")} || die {Sh(T(localize, "DeviceMismatch", "Device mismatch."))}");
        }
        foreach (var f in plan.Files) b.AppendLine($"[ -f \"$ROOT/{ImagePath(plan, f)}\" ] || die {Sh(T(localize, "MissingImage", "Missing image: {0}", f.FileName))}");
        if (plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
        {
            b.AppendLine(platform switch
            {
                "macOS" => "ZSTD=\"$TOOLS/zstd\"\n[ \"$(uname -m)\" = arm64 ] || ZSTD=\"$TOOLS/zstd-x86_64\"",
                "Linux" => "case \"$(uname -m)\" in x86_64|amd64) ZSTD=\"$TOOLS/zstd\" ;; aarch64|arm64) ZSTD=\"$TOOLS/zstd-arm64\" ;; loongarch64) ZSTD=\"$TOOLS/zstd-loong64\" ;; *) die \"Unsupported Linux architecture for zstd.\" ;; esac",
                _ => "ZSTD=\"$TOOLS/zstd\""
            });
            b.AppendLine($"[ -x \"$ZSTD\" ] || die {Sh(T(localize, "ZstdMissing", "zstd missing or not executable."))}");
        }
        var flashRoot = plan.PackSuper ? string.Join("\n", plan.Files.Select(f => (f, path: ImagePath(plan, f)))
            .Where(x => x.path.Contains('/'))
            .Select(x => $"    {x.f.FileName}) file=\"$ROOT/{x.path}\" ;;")) : string.Empty;
        b.AppendLine($"flash() {{\n  local target=\"$1\" name=\"$2\" file=\"$ROOT/{(plan.PackSuper ? "$2" : "images/$2")}\"\n  case \"$name\" in\n{flashRoot}\n    *.zst) file=\"$WORK/${{name%.zst}}.img\"; \"$ZSTD\" -d -o \"$file\" -- \"$ROOT/{(plan.PackSuper ? "$name" : "images/$name")}\" || die {Sh(T(localize, "DecompressionFailed", "Decompression failed."))} ;;\n  esac");
        b.AppendLine($"  if [ \"$target\" = super_empty ]; then\n    \"$TOOLS/fastboot\" wipe-super \"$file\" || die {Sh(T(localize, "WipeSuperFailed", "wipe-super failed."))}");
        b.AppendLine($"  elif [ \"$target\" = super ]; then\n{(plan.PackSuper ? ShellSuperSizeGuard(plan, localize) : "")}    \"$TOOLS/fastboot\" flash \"$target\" \"$file\" || die {Sh(T(localize, "SuperFlashFailed", "Super flash failed."))}\n  else");
        b.AppendLine($"    \"$TOOLS/fastboot\" {(plan.Slot.Length > 0 ? "--slot=" + plan.Slot + " " : "")}flash \"$target\" \"$file\" || die {Sh(T(localize, "FlashFailed", "Flash failed."))}\n  fi\n}}");
        b.AppendLine(ShellMode(localize));
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var optional = group.Length == 1 && group[0].Optional;
            var defaultIndex = optional && !group[0].Default ? 0 : Array.FindIndex(group, f => f.Default) + 1;
            if (defaultIndex < 1 && !optional) defaultIndex = 1;
            if (group.Length == 1 && !optional) { b.AppendLine($"choice{i}=1"); continue; }
            b.AppendLine($"printf '%s\\n' {Sh(T(localize, "Choose", "Choose {0}", group[0].Partition))}");
            if (optional) b.AppendLine($"printf '%s\\n' {Sh(T(localize, "Skip", "0: Skip"))}");
            for (var j = 0; j < group.Length; j++) b.AppendLine($"printf '%s\\n' {Sh($"{j + 1}: {group[j].FileName}")}");
            var valid = string.Join('|', Enumerable.Range(optional ? 0 : 1, group.Length + (optional ? 1 : 0)));
            b.AppendLine($"while :; do\n  printf '%s' {Sh(T(localize, "ChoicePrompt", "Choice [{0}]: ", defaultIndex))}\n  IFS= read -r answer || die {Sh(T(localize, "InputCancelled", "Input cancelled."))}\n  answer=${{answer:-{defaultIndex}}}\n  case \"$answer\" in {valid}) choice{i}=\"$answer\"; break ;; *) printf '%s\\n' {Sh(T(localize, "InvalidChoice", "Invalid choice."))} ;; esac\ndone");
        }
        if (plan.AskWipe) b.AppendLine($"printf '%s' {Sh(T(localize, "AskWipe", "Erase userdata and metadata? [y/N]: "))}\nIFS= read -r wipe || die {Sh(T(localize, "InputCancelled", "Input cancelled."))}");
        b.AppendLine($"printf '%s' {Sh(T(localize, "AskStart", "Start flashing? [y/N]: "))}\nIFS= read -r confirm || die {Sh(T(localize, "InputCancelled", "Input cancelled."))}\ncase \"$confirm\" in y|Y) ;; *) exit 0 ;; esac");
        foreach (var i in Enumerable.Range(0, groups.Length).OrderBy(i => groups[i].Any(NeedsFastbootd)))
        {
            b.AppendLine($"case \"$choice{i}\" in");
            for (var j = 0; j < groups[i].Length; j++) b.AppendLine($"  {j + 1}) ensure_mode {(NeedsFastbootd(groups[i][j]) ? "yes" : "no")}; flash {Sh(groups[i][j].Partition)} {Sh(groups[i][j].FileName)} ;;");
            b.AppendLine("esac");
        }
        if (plan.AskWipe) b.AppendLine($"case \"$wipe\" in y|Y) \"$TOOLS/fastboot\" erase metadata && \"$TOOLS/fastboot\" erase userdata || die {Sh(T(localize, "EraseFailed", "Erase failed."))} ;; esac");
        if (plan.Reboot) b.AppendLine($"\"$TOOLS/fastboot\" reboot || die {Sh(T(localize, "RebootFailed", "Reboot failed."))}");
        b.AppendLine($"printf '%s\\n' {Sh(T(localize, "Completed", "Completed."))}");
        return b.ToString().Replace("\r\n", "\n");
    }

    private static string Batch(DeliveryPlan plan, DeliveryFile[][] groups, Func<string, string>? localize)
    {
        var b = new StringBuilder("@echo off\r\nsetlocal DisableDelayedExpansion\r\nset \"WORK=\"\r\nchcp 65001 >nul\r\nset \"ROOT=%~dp0\"\r\nset \"TOOLS=%~dp0bin\\Windows\"\r\n");
        foreach (var line in BannerLines(plan, localize)) b.AppendLine(BatchEcho(line));
        foreach (var line in PrerequisiteLines(localize, false)) b.AppendLine(BatchEcho(line));
        b.AppendLine(BatchIntegrity(plan));
        b.AppendLine("if not exist \"%TOOLS%\\fastboot.exe\" goto failed\nset \"WORK=%TEMP%\\uotan-flash-%RANDOM%-%RANDOM%\"\nif exist \"%WORK%\" exit /b 1\nmkdir \"%WORK%\" || exit /b 1");
        b.AppendLine("\"%TOOLS%\\fastboot.exe\" devices >\"%WORK%\\devices.txt\" 2>&1\nif errorlevel 1 goto failed\nset COUNT=0\nfor /f \"usebackq tokens=1\" %%D in (\"%WORK%\\devices.txt\") do set /a COUNT+=1 >nul\nif not \"%COUNT%\"==\"1\" goto failed");
        if (plan.DeviceCodename.Length > 0)
        {
            b.AppendLine("\"%TOOLS%\\fastboot.exe\" getvar product >\"%WORK%\\product.txt\" 2>&1\nif errorlevel 1 goto failed");
            b.AppendLine($"findstr /r /x /c:\"product: *{plan.DeviceCodename.Replace(".", @"\.")}\" /c:\"(bootloader) product: *{plan.DeviceCodename.Replace(".", @"\.")}\" \"%WORK%\\product.txt\" >nul\nif errorlevel 1 goto failed");
        }
        foreach (var f in plan.Files) b.AppendLine($"if not exist \"%ROOT%{ImagePath(plan, f).Replace('/', '\\')}\" goto failed");
        if (plan.Files.Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
            b.AppendLine("if not exist \"%TOOLS%\\zstd.exe\" goto failed");
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var optional = group.Length == 1 && group[0].Optional;
            if (group.Length == 1 && !optional) { b.AppendLine($"set CHOICE{i}=1"); continue; }
            b.AppendLine($"echo {T(localize, "Choose", "Choose {0}", group[0].Partition)}");
            if (optional) b.AppendLine($"echo {T(localize, "Skip", "0: Skip")}");
            for (var j = 0; j < group.Length; j++) b.AppendLine($"echo {j + 1}: {group[j].FileName}");
            var keys = string.Concat(Enumerable.Range(optional ? 0 : 1, group.Length + (optional ? 1 : 0)));
            b.AppendLine($"choice /c {keys} /n /m \"{T(localize, "Choice", "Choice: ")}\"\nif errorlevel 255 goto failed\nif errorlevel 1 (goto chosen{i}) else (goto cancelled)\n:chosen{i}\nset CHOICE{i}=%ERRORLEVEL%");
            if (optional) b.AppendLine($"set /a CHOICE{i}-=1 >nul");
        }
        if (plan.AskWipe) b.AppendLine($"choice /c NY /n /m \"{T(localize, "AskWipeBatch", "Erase userdata and metadata? [N/Y]: ")}\"\nif errorlevel 255 goto failed\nif errorlevel 1 (set WIPE=%ERRORLEVEL%) else (goto cancelled)");
        b.AppendLine($"choice /c NY /n /m \"{T(localize, "AskStartBatch", "Start flashing? [N/Y]: ")}\"\nif errorlevel 255 goto failed\nif not errorlevel 2 goto cancelled");
        foreach (var i in Enumerable.Range(0, groups.Length).OrderBy(i => groups[i].Any(NeedsFastbootd)))
            for (var j = 0; j < groups[i].Length; j++)
            {
                b.AppendLine($"if not \"%CHOICE{i}%\"==\"{j + 1}\" goto skip{i}_{j}\ncall :ensure_mode {(NeedsFastbootd(groups[i][j]) ? "yes" : "no")}\nif errorlevel 1 goto failed\ncall :flash {groups[i][j].Partition} {groups[i][j].FileName}\nif errorlevel 1 goto failed\n:skip{i}_{j}");
            }
        if (plan.AskWipe) b.AppendLine("if not \"%WIPE%\"==\"2\" goto no_wipe\n\"%TOOLS%\\fastboot.exe\" erase metadata\nif errorlevel 1 goto failed\n\"%TOOLS%\\fastboot.exe\" erase userdata\nif errorlevel 1 goto failed\n:no_wipe");
        if (plan.Reboot) b.AppendLine("\"%TOOLS%\\fastboot.exe\" reboot\nif errorlevel 1 goto failed");
        b.AppendLine($"echo {T(localize, "Completed", "Completed.")}\n:cancelled\nif defined WORK rmdir /s /q \"%WORK%\"\nexit /b 0\n:failed\necho {T(localize, "DeliveryFailed", "Delivery failed. Check device, images and tools.")}\nif defined WORK if exist \"%WORK%\" rmdir /s /q \"%WORK%\"\nexit /b 1");
        var batchCases = plan.PackSuper ? string.Join("\n", plan.Files.Select(f => (f, path: ImagePath(plan, f)))
            .Where(x => x.path.Contains('/'))
            .Select(x => $"if /i \"%~2\"==\"{x.f.FileName}\" set \"IMAGE=%ROOT%{x.path.Replace('/', '\\')}\"")) : string.Empty;
        b.AppendLine($":flash\nset \"IMAGE=%ROOT%{(plan.PackSuper ? "%~2" : "images\\%~2")}\"\n{batchCases}\nif /i not \"%~x2\"==\".zst\" goto flash_ready\nset \"IMAGE=%WORK%\\%~n2.img\"\n\"%TOOLS%\\zstd.exe\" -d \"%ROOT%{(plan.PackSuper ? "%~2" : "images\\%~2")}\" -o \"%IMAGE%\"\nif errorlevel 1 exit /b 1\n:flash_ready\nif \"%~1\"==\"super_empty\" goto wipe_super\nif \"%~1\"==\"super\" goto flash_super");
        b.AppendLine($"\"%TOOLS%\\fastboot.exe\" {(plan.Slot.Length > 0 ? "--slot=" + plan.Slot + " " : "")}flash %~1 \"%IMAGE%\"\nexit /b %ERRORLEVEL%\n:flash_super\n{(plan.PackSuper ? "call :ensure_super_size\nif errorlevel 1 exit /b 1\n" : "")}\"%TOOLS%\\fastboot.exe\" flash super \"%IMAGE%\"\nexit /b %ERRORLEVEL%\n:wipe_super\n\"%TOOLS%\\fastboot.exe\" wipe-super \"%IMAGE%\"\nexit /b %ERRORLEVEL%");
        b.AppendLine(BatchMode());
        if (plan.PackSuper) b.Append(BatchSuperSizeGuard(plan));
        return b.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}