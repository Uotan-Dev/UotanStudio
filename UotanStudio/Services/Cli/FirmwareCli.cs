using System.Text;
using UotanStudio.Firmware;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Services.Firmware;

namespace UotanStudio.Services.Cli;

/// <summary>
/// Exit codes for the firmware CLI.
/// <para>固件 CLI 退出码。</para>
/// </summary>
public static class FirmwareCliExitCodes
{
    /// <summary>Operation succeeded.</summary>
    /// <para>操作成功。</para>
    public const int Ok = 0;

    /// <summary>Operation failed (handler error, file not found, unsupported format...).</summary>
    /// <para>操作失败（处理器错误、文件未找到、不支持格式……）。</para>
    public const int Failure = 1;

    /// <summary>Bad usage (missing/extra args, unknown flag/subcommand).</summary>
    /// <para>用法错误（缺参数/多参数/未知标志/未知子命令）。</para>
    public const int Usage = 2;
}

/// <summary>
/// Entry point for the <c>firmware</c> CLI subcommand. Parses arguments, ensures firmware
/// handlers are registered, dispatches to the resolved <see cref="IFormatHandler"/>, and
/// returns a process exit code. All output goes to <see cref="Console"/>; no Avalonia UI is
/// started. AOT-friendly (no reflection).
/// <para>固件 CLI 子命令入口。解析参数、确保处理器已注册、分发到解析出的
/// <see cref="IFormatHandler"/> 并返回进程退出码。全部输出走 <see cref="Console"/>，
/// 不启动 Avalonia UI。AOT 友好（无反射）。</para>
/// </summary>
public static class FirmwareCli
{
    /// <summary>
    /// Runs the firmware CLI with the given arguments (the tokens after the literal
    /// <c>firmware</c> token). Returns a process exit code.
    /// </summary>
    /// <para>以给定参数运行固件 CLI（字面量 <c>firmware</c> 之后的 token），返回进程退出码。</para>
    public static int Run(string[]? args)
    {
        // 保证 Windows 控制台能正确显示中文/非 ASCII 文本。
        // Ensure the Windows console renders Chinese/non-ASCII text correctly.
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 控制台不支持编码则忽略 */ }

        var parse = FirmwareCliParser.Parse(args);
        if (parse.IsError)
        {
            Console.Error.WriteLine(parse.Error);
            PrintUsage(Console.Error);
            return FirmwareCliExitCodes.Usage;
        }

        var cmd = parse.Command!;
        if (cmd.HelpRequested)
        {
            PrintUsage(Console.Out);
            return FirmwareCliExitCodes.Ok;
        }

        if (cmd.VersionRequested)
        {
            var version = typeof(FirmwareCli).Assembly.GetName().Version;
            Console.WriteLine($"UotanStudio firmware CLI {version?.ToString(3) ?? "1.0.0"}");
            return FirmwareCliExitCodes.Ok;
        }

        // 任何子命令都需要已注册的格式处理器。
        // Any subcommand requires registered format handlers.
        FirmwareFormatRegistry.RegisterAll();

        return cmd.Verb switch
        {
            FirmwareCliVerb.List => RunList(),
            FirmwareCliVerb.Info => RunInfo(cmd),
            FirmwareCliVerb.Unpack => RunUnpack(cmd),
            FirmwareCliVerb.Pack => RunPack(cmd),
            FirmwareCliVerb.Flash => RunFlash(cmd),
            FirmwareCliVerb.DisableVerification => RunDisableVerification(cmd),
            FirmwareCliVerb.Verify => RunVerify(cmd),
            FirmwareCliVerb.Extract => RunExtract(cmd),
            FirmwareCliVerb.SuperUnpack => RunSuperUnpack(cmd),
            FirmwareCliVerb.SuperPack => RunSuperPack(cmd),
            FirmwareCliVerb.Erase => RunErase(cmd),
            FirmwareCliVerb.Reboot => RunReboot(cmd),
            FirmwareCliVerb.DecompileDex => RunDecompileDex(cmd),
            FirmwareCliVerb.DisassembleDex => RunDisassembleDex(cmd),
            FirmwareCliVerb.Checksum => RunChecksum(cmd),
            _ => FirmwareCliExitCodes.Failure,
        };
    }

    /// <summary>
    /// Patches a VBMeta image to disable AVB verification (flags set, authentication block zeroed;
    /// no private key needed).
    /// <para>补丁 VBMeta 镜像以禁用 AVB 校验（置位标志并清零认证块；无需私钥）。</para>
    /// </summary>
    private static int RunDisableVerification(FirmwareCliCommand cmd)
    {
        var input = cmd.InputPath!;
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"File not found: {input}  文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var output = cmd.OutputPath!;
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var result = FirmwareService.Instance
            .DisableVbmetaVerificationAsync(input, output, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Flashes an image to a device partition headlessly: enumerates devices via
    /// <see cref="Devices.DeviceMonitorService.Scan"/>, picks the target (unique device or
    /// <c>--serial</c>), then delegates to <see cref="Flash.FlashService.FlashImageAsync"/>.
    /// <para>无头刷写镜像到设备分区：经 <see cref="Devices.DeviceMonitorService.Scan"/> 枚举设备，
    /// 选择目标（唯一设备或 <c>--serial</c> 指定），再委托 <see cref="Flash.FlashService.FlashImageAsync"/>。</para>
    /// </summary>
    private static int RunFlash(FirmwareCliCommand cmd)
    {
        var image = cmd.InputPath!;
        if (!File.Exists(image))
        {
            Console.Error.WriteLine($"Image file not found: {image}  镜像文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var devices = Devices.DeviceMonitorService.Instance.Scan();
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No flashable device detected  未检测到可刷写设备");
            return FirmwareCliExitCodes.Failure;
        }

        Devices.DetectedDevice? target = null;
        if (!string.IsNullOrWhiteSpace(cmd.Serial))
        {
            target = devices.FirstOrDefault(d => string.Equals(d.Serial, cmd.Serial, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                Console.Error.WriteLine($"Device with serial '{cmd.Serial}' not found  未找到指定序列号设备");
                PrintDevices(devices);
                return FirmwareCliExitCodes.Failure;
            }
        }
        else if (devices.Count > 1)
        {
            Console.Error.WriteLine("Multiple devices detected; specify --serial  检测到多台设备，请用 --serial 指定");
            PrintDevices(devices);
            return FirmwareCliExitCodes.Failure;
        }
        else
        {
            target = devices[0];
        }

        var device = new ViewModels.Devices.DeviceInfo(target);
        Console.WriteLine($"Flashing '{cmd.Partition}' ← {image} on {target.Serial} ({target.Protocol})  开始刷写");

        var result = Flash.FlashService.Default.FlashImageAsync(
            device,
            cmd.Partition!,
            image,
            new Progress<Flash.FlashProgress>(p =>
                Console.WriteLine($"[{p.Partition ?? cmd.Partition}] {p.Percent,3}%  {p.Status}")),
            CancellationToken.None).GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Prints the detected device list to stderr (used for multi-device ambiguity errors).
    /// <para>向 stderr 打印检测到的设备列表（用于多设备歧义提示）。</para>
    /// </summary>
    private static void PrintDevices(IReadOnlyList<Devices.DetectedDevice> devices)
    {
        foreach (var d in devices)
        {
            Console.Error.WriteLine($"  - {d.Serial} ({d.Protocol})");
        }
    }

    private static int RunList()
    {
        var handlers = FormatDetector.RegisteredHandlers;
        Console.WriteLine($"Registered firmware formats ({handlers.Count}):");
        Console.WriteLine("  已注册固件格式：");
        Console.WriteLine();
        Console.WriteLine("  Name             Extensions");
        Console.WriteLine("  ----             ----------");
        foreach (var h in handlers)
        {
            var exts = h.FileExtensions.Count == 0
                ? "(none)"
                : string.Join(", ", h.FileExtensions);
            Console.WriteLine($"  {Pad(h.FormatName, 17)}{exts}");
        }

        return FirmwareCliExitCodes.Ok;
    }

    private static int RunInfo(FirmwareCliCommand cmd)
    {
        var path = cmd.InputPath!;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}  文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var handler = FormatDetector.Detect(path);
        if (handler is null)
        {
            Console.Error.WriteLine($"Unrecognized firmware format: {path}  无法识别的固件格式");
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine($"Format: {handler.FormatName}");
        var result = handler.ReadMetadataAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine($"Summary: {result.Summary}");
        if (result.Entries.Count > 0)
        {
            Console.WriteLine($"Entries ({result.Entries.Count}):");
            foreach (var e in result.Entries)
            {
                Console.WriteLine($"  - {e}");
            }
        }

        if (result.Metadata.Count > 0)
        {
            Console.WriteLine("Metadata:");
            foreach (var kv in result.Metadata)
            {
                Console.WriteLine($"  {kv.Key}: {kv.Value}");
            }
        }

        if (result.Components.Count > 0)
        {
            Console.WriteLine($"Components ({result.Components.Count}):");
            foreach (var c in result.Components)
            {
                Console.WriteLine($"  - {c.Name} ({c.SizeBytes} bytes)");
            }
        }

        return FirmwareCliExitCodes.Ok;
    }

    private static int RunUnpack(FirmwareCliCommand cmd)
    {
        var path = cmd.InputPath!;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}  文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var handler = cmd.FormatName is null
            ? FormatDetector.Detect(path)
            : ResolveByFormatName(cmd.FormatName);

        if (handler is null)
        {
            Console.Error.WriteLine(
                cmd.FormatName is null
                    ? $"Unrecognized firmware format: {path}  无法识别的固件格式"
                    : $"Unknown format name: {cmd.FormatName}  未知格式名");
            return FirmwareCliExitCodes.Failure;
        }

        var outputDir = cmd.OutputPath!;
        Directory.CreateDirectory(outputDir);

        var options = new UnpackOptions
        {
            Overwrite = !cmd.NoOverwrite,
            Progress = new ConsoleProgress("unpack"),
        };

        var result = handler.UnpackAsync(path, outputDir, options, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        return FirmwareCliExitCodes.Ok;
    }

    private static int RunPack(FirmwareCliCommand cmd)
    {
        var input = cmd.InputPath!;
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            Console.Error.WriteLine($"Input not found: {input}  输入未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var output = cmd.OutputPath!;
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        IReadonlyFormatHandler? handler = null;
        if (cmd.FormatName is not null)
        {
            handler = ResolveByFormatName(cmd.FormatName);
            if (handler is null)
            {
                Console.Error.WriteLine($"Unknown format name: {cmd.FormatName}  未知格式名");
                return FirmwareCliExitCodes.Failure;
            }
        }

        // 未显式指定 --format 时，按输入类型解析：
        // 目录 → 默认 EXT4（系统分区常见）；文件 → 按内容检测，回退到原样复制。
        // When --format is not given: directory defaults to EXT4; file is content-detected.
        handler ??= Directory.Exists(input)
            ? ResolveByFormatName("EXT4")
            : FormatDetector.Detect(input);

        if (handler is null)
        {
            Console.Error.WriteLine($"Cannot determine target format for input: {input}  无法确定目标格式");
            return FirmwareCliExitCodes.Failure;
        }

        var result = ((IWritableFormatHandler)handler).PackAsync(input, output, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Verifies AVB signature on a VBMeta image headlessly.
    /// <para>无头校验 VBMeta 镜像的 AVB 签名。</para>
    /// </summary>
    private static int RunVerify(FirmwareCliCommand cmd)
    {
        var input = cmd.InputPath!;
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"File not found: {input}  文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        if (!string.IsNullOrWhiteSpace(cmd.PublicKeyPath) && !File.Exists(cmd.PublicKeyPath))
        {
            Console.Error.WriteLine($"Public key file not found: {cmd.PublicKeyPath}  公钥文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var result = FirmwareService.Instance
            .VerifyAvbAsync(input, cmd.PublicKeyPath, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Extracts partitions from an Android OTA payload.bin headlessly.
    /// <para>无头从 Android OTA payload.bin 中提取分区。</para>
    /// </summary>
    private static int RunExtract(FirmwareCliCommand cmd)
    {
        var input = cmd.InputPath!;
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"File not found: {input}  文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var outputDir = cmd.OutputPath!;
        Directory.CreateDirectory(outputDir);

        var result = FirmwareService.Instance
            .ExtractPayloadAsync(input, outputDir, cmd.Partition)
            .GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        if (result.Entries is { Count: > 0 })
        {
            Console.WriteLine($"Extracted partitions ({result.Entries.Count}):");
            foreach (var e in result.Entries)
            {
                Console.WriteLine($"  - {e}");
            }
        }

        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Unpacks an Android super (logical partition) image headlessly.
    /// <para>无头解包 Android super（逻辑分区）镜像。</para>
    /// </summary>
    private static int RunSuperUnpack(FirmwareCliCommand cmd)
    {
        var input = cmd.InputPath!;
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"File not found: {input}  文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var outputDir = cmd.OutputPath!;
        Directory.CreateDirectory(outputDir);

        var result = FirmwareService.Instance
            .UnpackSuperAsync(input, outputDir, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        if (result.Entries is { Count: > 0 })
        {
            Console.WriteLine($"Unpacked partitions ({result.Entries.Count}):");
            foreach (var e in result.Entries)
            {
                Console.WriteLine($"  - {e}");
            }
        }

        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Packs directory contents into an Android super image headlessly.
    /// Scans the input directory for .img files, infers partition names from filenames,
    /// and synthesizes a super image via <see cref="FirmwareService.SynthesizeSuperAsync"/>.
    /// <para>无头将目录内容打包为 Android super 镜像。扫描输入目录的 .img 文件，
    /// 从文件名推断分区名，经 <see cref="FirmwareService.SynthesizeSuperAsync"/> 合成。</para>
    /// </summary>
    private static int RunSuperPack(FirmwareCliCommand cmd)
    {
        var input = cmd.InputPath!;
        if (!Directory.Exists(input))
        {
            Console.Error.WriteLine($"Directory not found: {input}  目录未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var output = cmd.OutputPath!;
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Scan for partition images in the directory.
        var imageFiles = Directory.GetFiles(input, "*.img")
            .Concat(Directory.GetFiles(input, "*.bin"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (imageFiles.Length == 0)
        {
            Console.Error.WriteLine($"No .img/.bin files found in: {input}  目录中未找到 .img/.bin 文件");
            return FirmwareCliExitCodes.Failure;
        }

        var partitions = imageFiles.Select(f => new Firmware.SuperPartitionInput(
            Path.GetFileNameWithoutExtension(f),
            f)).ToList();

        // Calculate device size: sum of all partition sizes + 4 MiB metadata overhead, aligned to 1 MiB.
        long totalSize = partitions.Sum(p => new FileInfo(p.Path).Length);
        var deviceSize = (ulong)((totalSize + 4 * 1024 * 1024 + 1024 * 1024 - 1) & ~(1024 * 1024 - 1));

        Console.WriteLine($"Packing {partitions.Count} partitions into super image ({deviceSize / 1024 / 1024} MiB):");
        foreach (var p in partitions)
        {
            Console.WriteLine($"  - {p.Name} ({new FileInfo(p.Path).Length / 1024} KiB)");
        }

        var result = FirmwareService.Instance
            .SynthesizeSuperAsync(partitions, output, deviceSize, "default", CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Erases a device partition headlessly via fastboot.
    /// <para>无头通过 fastboot 擦除设备分区。</para>
    /// </summary>
    private static int RunErase(FirmwareCliCommand cmd)
    {
        var devices = Devices.DeviceMonitorService.Instance.Scan();
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No flashable device detected  未检测到可刷写设备");
            return FirmwareCliExitCodes.Failure;
        }

        Devices.DetectedDevice? target = null;
        if (!string.IsNullOrWhiteSpace(cmd.Serial))
        {
            target = devices.FirstOrDefault(d => string.Equals(d.Serial, cmd.Serial, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                Console.Error.WriteLine($"Device with serial '{cmd.Serial}' not found  未找到指定序列号设备");
                PrintDevices(devices);
                return FirmwareCliExitCodes.Failure;
            }
        }
        else if (devices.Count > 1)
        {
            Console.Error.WriteLine("Multiple devices detected; specify --serial  检测到多台设备，请用 --serial 指定");
            PrintDevices(devices);
            return FirmwareCliExitCodes.Failure;
        }
        else
        {
            target = devices[0];
        }

        var device = new ViewModels.Devices.DeviceInfo(target);
        Console.WriteLine($"Erasing '{cmd.Partition}' on {target.Serial} ({target.Protocol})  开始擦除");

        var result = Flash.FlashService.Default.ErasePartitionAsync(
            device,
            cmd.Partition!,
            new Progress<Flash.FlashProgress>(p =>
                Console.WriteLine($"[{p.Partition ?? cmd.Partition}] {p.Percent,3}%  {p.Status}")),
            CancellationToken.None).GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Reboots a device into a specified mode headlessly.
    /// <para>无头将设备重启到指定模式。</para>
    /// </summary>
    private static int RunReboot(FirmwareCliCommand cmd)
    {
        var devices = Devices.DeviceMonitorService.Instance.Scan();
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No device detected  未检测到设备");
            return FirmwareCliExitCodes.Failure;
        }

        Devices.DetectedDevice? target = null;
        if (!string.IsNullOrWhiteSpace(cmd.Serial))
        {
            target = devices.FirstOrDefault(d => string.Equals(d.Serial, cmd.Serial, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                Console.Error.WriteLine($"Device with serial '{cmd.Serial}' not found  未找到指定序列号设备");
                PrintDevices(devices);
                return FirmwareCliExitCodes.Failure;
            }
        }
        else if (devices.Count > 1)
        {
            Console.Error.WriteLine("Multiple devices detected; specify --serial  检测到多台设备，请用 --serial 指定");
            PrintDevices(devices);
            return FirmwareCliExitCodes.Failure;
        }
        else
        {
            target = devices[0];
        }

        var mode = cmd.RebootMode ?? "system";
        Console.WriteLine($"Rebooting {target.Serial} into '{mode}' ({target.Protocol})  正在重启");

        var success = Devices.DeviceMonitorService.Instance
            .RebootAsync(target, mode, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!success)
        {
            Console.Error.WriteLine($"Failed to reboot device into '{mode}'  重启失败");
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine($"Device rebooting into '{mode}'  设备正在重启");
        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Decompiles a DEX file to pseudo-Java source headlessly.
    /// <para>无头将 DEX 文件反编译为伪 Java 源码。</para>
    /// </summary>
    private static int RunDecompileDex(FirmwareCliCommand cmd)
    {
        var input = cmd.InputPath!;
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"File not found: {input}  文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var output = cmd.OutputPath;
        if (string.IsNullOrWhiteSpace(output))
        {
            var name = Path.GetFileNameWithoutExtension(input);
            output = Path.Combine(
                Path.GetDirectoryName(input) ?? ".",
                (string.IsNullOrEmpty(name) ? "dex" : name) + "_decompiled.java");
        }

        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var result = Firmware.DexService.Instance
            .DecompileDexAsync(input, output, ct: CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Disassembles a DEX file to Smali text headlessly.
    /// <para>无头将 DEX 文件反汇编为 Smali 文本。</para>
    /// </summary>
    private static int RunDisassembleDex(FirmwareCliCommand cmd)
    {
        var input = cmd.InputPath!;
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"File not found: {input}  文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        var outputDir = cmd.OutputPath;

        var result = Firmware.DexService.Instance
            .DisassembleDexAsync(input, outputDir, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Summary);
            return FirmwareCliExitCodes.Failure;
        }

        Console.WriteLine(result.Summary);
        return FirmwareCliExitCodes.Ok;
    }

    /// <summary>
    /// Computes a streaming SHA-256 checksum of a file headlessly.
    /// <para>无头计算文件的流式 SHA-256 校验和。</para>
    /// </summary>
    private static int RunChecksum(FirmwareCliCommand cmd)
    {
        var input = cmd.InputPath!;
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"File not found: {input}  文件未找到");
            return FirmwareCliExitCodes.Failure;
        }

        try
        {
            using var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(stream);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            var size = stream.Length;
            Console.WriteLine($"SHA-256: {hex}");
            Console.WriteLine($"Size:    {size} bytes ({size / 1024.0 / 1024.0:F2} MiB)");
            Console.WriteLine($"File:    {Path.GetFileName(input)}");
            return FirmwareCliExitCodes.Ok;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Checksum failed: {ex.Message}  校验和计算失败");
            return FirmwareCliExitCodes.Failure;
        }
    }

    private static IReadonlyFormatHandler? ResolveByFormatName(string formatName)
    {
        var name = formatName.Trim();
        foreach (var h in FormatDetector.RegisteredHandlers)
        {
            if (string.Equals(h.FormatName, name, StringComparison.OrdinalIgnoreCase))
            {
                return h;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a registered format handler by its <see cref="IFormatHandler.FormatName"/>
    /// (case-insensitive). Exposed for tests that need to exercise a specific handler.
    /// </summary>
    /// <para>按 <see cref="IFormatHandler.FormatName"/>（不区分大小写）解析已注册的格式处理器；供测试使用。</para>
    public static IReadonlyFormatHandler? ResolveByFormatNameForTest(string formatName)
        => ResolveByFormatName(formatName);

    private static string Pad(string s, int width)
    {
        if (s.Length >= width)
        {
            return s;
        }

        return s + new string(' ', width - s.Length);
    }

    /// <summary>
    /// Prints the firmware CLI usage to the given writer.
    /// <para>向给定文本写入器打印固件 CLI 用法。</para>
    /// </summary>
    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine();
        writer.WriteLine("UotanStudio firmware CLI  固件命令行工具");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  firmware <command> [options]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  list                              List registered firmware formats");
        writer.WriteLine("  info <file>                       Detect format and read metadata");
        writer.WriteLine("  unpack <file> -o <dir>            Unpack a firmware file to a directory");
        writer.WriteLine("  pack <input> -o <file>            Pack a directory/file into a firmware file");
        writer.WriteLine("  verify <file> [-k <key>]          Verify AVB signature on a vbmeta/image");
        writer.WriteLine("  extract <payload> -o <dir>        Extract partitions from payload.bin");
        writer.WriteLine("  super-unpack <super> -o <dir>     Unpack a super (logical partition) image");
        writer.WriteLine("  super-pack <dir> -o <super>       Pack partition images into a super image");
        writer.WriteLine("  flash <image> -p <partition>      Flash an image to a device partition");
        writer.WriteLine("  erase <partition> [-d <serial>]   Erase a device partition (fastboot)");
        writer.WriteLine("  reboot [mode] [-d <serial>]       Reboot device (system/bootloader/recovery/edl)");
        writer.WriteLine("  decompile-dex <file.dex> [-o <f>] Decompile DEX to pseudo-Java source");
        writer.WriteLine("  disassemble-dex <file> [-o <dir>] Disassemble DEX to Smali text");
        writer.WriteLine("  checksum <file>                   Compute streaming SHA-256 checksum");
        writer.WriteLine("  disable-verification <v> -o <f>   Patch vbmeta to disable AVB verification");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -o, --output-dir <dir>            Output directory");
        writer.WriteLine("  -o, --output <file>               Output file");
        writer.WriteLine("  -f, --format <name>               Explicit format (e.g. ext4, erofs, payload, boot)");
        writer.WriteLine("      --no-overwrite                Do not overwrite existing files (unpack)");
        writer.WriteLine("  -p, --partition <name>            Target partition name (flash/extract)");
        writer.WriteLine("  -d, --serial <serial>             Target device serial");
        writer.WriteLine("  -k, --public-key <path>           Trusted AVB public key (verify)");
        writer.WriteLine("  -h, --help                        Show this help and exit");
        writer.WriteLine("  -v, --version                     Show version and exit");
        writer.WriteLine();
        writer.WriteLine("Exit codes:");
        writer.WriteLine("  0  Success");
        writer.WriteLine("  1  Failure (handler error, unsupported format, file not found)");
        writer.WriteLine("  2  Usage error (missing/extra args, unknown flag/subcommand)");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  firmware list");
        writer.WriteLine("  firmware info boot.img");
        writer.WriteLine("  firmware unpack boot.img -o boot_out/");
        writer.WriteLine("  firmware unpack payload.bin -o payload_out/ -f payload");
        writer.WriteLine("  firmware pack system_dir/ -o system.img -f ext4");
        writer.WriteLine("  firmware pack boot_out/ -o boot_new.img -f boot");
        writer.WriteLine("  firmware verify vbmeta.img");
        writer.WriteLine("  firmware verify vbmeta.img -k public_key.bin");
        writer.WriteLine("  firmware extract payload.bin -o extracted/ -p system");
        writer.WriteLine("  firmware super-unpack super.img -o super_out/");
        writer.WriteLine("  firmware super-pack super_out/ -o super_new.img");
        writer.WriteLine("  firmware flash boot.img -p boot");
        writer.WriteLine("  firmware flash system.img -p system -d 1234567890ABCDEF");
        writer.WriteLine("  firmware erase userdata");
        writer.WriteLine("  firmware erase cache -d 1234567890ABCDEF");
        writer.WriteLine("  firmware reboot bootloader");
        writer.WriteLine("  firmware reboot edl -d 1234567890ABCDEF");
        writer.WriteLine("  firmware disable-verification vbmeta.img -o vbmeta_disabled.img");
    }

    /// <summary>
    /// A console-backed <see cref="IFirmwareProgress"/> that prints stage messages to stdout.
    /// <para>基于控制台的 <see cref="IFirmwareProgress"/>，将阶段消息打印到 stdout。</para>
    /// </summary>
    private sealed class ConsoleProgress : IFirmwareProgress
    {
        private readonly string _tag;
        private double _lastPercent = -1;

        public ConsoleProgress(string tag)
        {
            _tag = tag;
        }

        public void Report(double percent, string message)
        {
            // 只在整 5% 边界或最终消息时打印，避免刷屏。
            // Print only at 5% boundaries or final message to avoid flooding the console.
            var p = (int)Math.Round(percent);
            if (p % 5 == 0 && p != _lastPercent)
            {
                _lastPercent = p;
                Console.WriteLine($"[{_tag}] {p,3}%  {message}");
            }
        }
    }
}