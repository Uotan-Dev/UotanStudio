using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Flash;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Services.Mcp;

// 消除 UotanStudio.Firmware.Common 与 UotanStudio.Services.Firmware 中同名 FirmwareOpResult 的歧义：
// 本文件的工具统一使用 Services 层的结果类型（FirmwareService 各方法的返回值）。
// Disambiguate the same-named FirmwareOpResult in UotanStudio.Firmware.Common and
// UotanStudio.Services.Firmware: this file consistently uses the Services-layer result type
// (the return type of the FirmwareService methods).
using FirmwareOpResult = UotanStudio.Services.Firmware.FirmwareOpResult;

/// <summary>
/// <para>UotanStudioMcpTools 的固件操作工具部分：在既有 <c>firmware.info</c>（<see cref="McpHostService"/> 中定义）
/// 之外，新增固件解包/打包/刷写工具，供外部 AI 工具经 MCP 调用。工具输入实参由 MCP SDK 按方法签名
/// 生成 JSON Schema 并反序列化，输出为结构化 JSON 字符串；实现遵循 AOT 安全约束——内部只使用
/// <see cref="JsonObject"/>/<see cref="JsonNode"/> 构造输出，不引入反射。</para>
/// The firmware-operation part of UotanStudioMcpTools: in addition to the existing
/// <c>firmware.info</c> (defined in <see cref="McpHostService"/>), adds firmware unpack/pack/flash
/// tools callable by external AI tools over MCP. Tool arguments arrive as raw JSON deserialized by the
/// MCP SDK against the method signature (which also drives the input JSON Schema); results are
/// structured JSON strings. The implementation stays AOT-safe — outputs are built solely with
/// <see cref="JsonObject"/>/<see cref="JsonNode"/>, no reflection.
/// </summary>
public sealed partial class UotanStudioMcpTools
{
    /// <summary>
    /// <para>按格式解包固件文件（CPIO/TAR、OTA payload.bin、boot/vendor_boot、Oppo OFP/OPS/OZIP、
    /// Android super）。格式优先使用 <paramref name="format"/> 指定（名称/扩展名/别名），否则自动探测；
    /// 解包方法按格式分派到 <see cref="FirmwareService"/>（与 FirmwareInfoViewModel 一致）。
    /// <paramref name="extractFiles"/> 仅对 payload.bin 生效（取首个条目作为目标分区名），其余格式忽略。</para>
    /// Unpacks a firmware file by format (CPIO/TAR, OTA payload.bin, boot/vendor_boot, Oppo
    /// OFP/OPS/OZIP, Android super). The format is taken from <paramref name="format"/> when given
    /// (name/extension/alias), otherwise auto-detected; the unpack call is dispatched to
    /// <see cref="FirmwareService"/> per format (same dispatch as FirmwareInfoViewModel).
    /// <paramref name="extractFiles"/> is honored only for payload.bin (first entry selects the target
    /// partition) and ignored otherwise.
    /// </summary>
    /// <param name="filePath">固件文件路径。<para>Firmware file path.</para></param>
    /// <param name="outputDir">
    /// <para>可选输出目录；缺省为固件同目录下的 <c>&lt;文件名&gt;_unpacked</c>。</para>
    /// Optional output directory; defaults to <c>&lt;file-name&gt;_unpacked</c> next to the firmware.
    /// </param>
    /// <param name="format">
    /// <para>可选格式提示（如 cpio/payload/boot/oppo/super，或处理器格式名/扩展名）；缺省自动探测。</para>
    /// Optional format hint (e.g. cpio/payload/boot/oppo/super, or a handler format name/extension);
    /// auto-detected when omitted.
    /// </param>
    /// <param name="extractFiles">
    /// <para>可选条目/分区筛选；仅 payload.bin 生效（首个条目为分区名）。</para>
    /// Optional entry/partition filter; honored only for payload.bin (first entry is the partition).
    /// </param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>结构化 JSON 字符串（success/tool/format/filePath/outputDir/summary/entries/error）。</returns>
    [McpServerTool(Name = "firmware.unpack"), Description("Unpacks a firmware file by format (CPIO/TAR, OTA payload.bin, boot/vendor_boot, Oppo OFP/OPS/OZIP, Android super) and returns the output directory as structured JSON.")]
    public async Task<string> FirmwareUnpack(
        string filePath,
        string? outputDir = null,
        string? format = null,
        string[]? extractFiles = null,
        CancellationToken ct = default)
    {
        const string tool = "firmware.unpack";
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return ErrorJson(tool, $"File not found: {filePath}  文件不存在：{filePath}");
            }

            var handler = ResolveFormatHandler(format, filePath);
            if (handler is null)
            {
                return ErrorJson(tool, $"Unknown or unsupported firmware format: {format ?? filePath}  无法识别或不支持该固件格式。");
            }

            var output = string.IsNullOrWhiteSpace(outputDir)
                ? Path.Combine(Path.GetDirectoryName(filePath) ?? ".", Path.GetFileNameWithoutExtension(filePath) + "_unpacked")
                : outputDir;

            var result = await DispatchUnpackAsync(handler, filePath, output, extractFiles, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return ErrorJson(tool, result.Summary);
            }

            var json = new JsonObject
            {
                ["success"] = true,
                ["tool"] = tool,
                ["format"] = handler.FormatName,
                ["filePath"] = filePath,
                ["outputDir"] = output,
                ["summary"] = result.Summary,
            };
            if (result.Entries is { Count: > 0 })
            {
                var entries = new JsonArray();
                foreach (var entry in result.Entries)
                {
                    entries.Add(JsonValue.Create(entry));
                }

                json["entries"] = entries;
            }

            if (extractFiles is { Length: > 0 } && handler.FormatName != "Android OTA Payload (payload.bin)")
            {
                json["notice"] = "extractFiles is honored only for payload.bin (partition selection); ignored for this format.  extractFiles 仅对 payload.bin 格式生效（分区选择），本格式已忽略。";
            }

            return json.ToJsonString();
        }
        catch (OperationCanceledException)
        {
            return ErrorJson(tool, "Unpack cancelled. 解包已取消。");
        }
        catch (Exception ex)
        {
            return ErrorJson(tool, ex.Message);
        }
    }

    /// <summary>
    /// <para>把解包产物目录打包为固件镜像。支持格式：<c>cpio</c>（CPIO newc 归档）、
    /// <c>boot</c>（boot 镜像重打包，源目录需含 <c>header.json</c>）、<c>ext4</c>、<c>erofs</c>。
    /// <paramref name="format"/> 缺省时按输出扩展名推断（.cpio→cpio、.erofs→erofs、.ext4→ext4、
    /// 含 header.json 的源目录打包为 boot，其余回落到 cpio）。</para>
    /// Packs an unpacked directory into a firmware image. Supported formats: <c>cpio</c> (CPIO newc
    /// archive), <c>boot</c> (boot image repack; the source directory must contain
    /// <c>header.json</c>), <c>ext4</c>, <c>erofs</c>. When <paramref name="format"/> is omitted the
    /// format is inferred from the output extension (.cpio→cpio, .erofs→erofs, .ext4→ext4; a source
    /// with header.json packs as boot, anything else falls back to cpio).
    /// </summary>
    /// <param name="inputDir">
    /// <para>源目录（解包产物目录）。与 <paramref name="inputPath"/> 二选一，优先使用本参数。</para>
    /// The source directory (unpack output). Either this or <paramref name="inputPath"/> is required;
    /// this parameter takes precedence.
    /// </param>
    /// <param name="inputPath">
    /// <para>源路径兼容别名；<paramref name="inputDir"/> 为空时作为源目录使用。</para>
    /// Compatibility alias for the source path; used as the source directory when
    /// <paramref name="inputDir"/> is empty.
    /// </param>
    /// <param name="outputPath">输出镜像路径。<para>Output image path.</para></param>
    /// <param name="format">
    /// <para>可选格式（cpio/boot/ext4/erofs）；缺省按输出扩展名与源内容推断。</para>
    /// Optional format (cpio/boot/ext4/erofs); inferred from the output extension and source content
    /// when omitted.
    /// </param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>结构化 JSON 字符串（success/tool/format/inputDir/outputPath/sizeBytes/summary/error）。</returns>
    [McpServerTool(Name = "firmware.pack"), Description("Packs an unpacked directory into a firmware image (cpio, boot, ext4 or erofs) and returns the output path and size as structured JSON.")]
    public async Task<string> FirmwarePack(
        string? inputDir,
        string? inputPath,
        string outputPath,
        string? format = null,
        CancellationToken ct = default)
    {
        const string tool = "firmware.pack";
        try
        {
            var source = string.IsNullOrWhiteSpace(inputDir) ? inputPath : inputDir;
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            {
                return ErrorJson(tool, $"Source directory not found (inputDir/inputPath): {source ?? "<empty>"}  源目录不存在：{source ?? "<empty>"}");
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return ErrorJson(tool, "Output path is empty. 输出路径为空。");
            }

            var parentDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            var resolved = ResolvePackFormat(format, source, outputPath);
            if (resolved is null)
            {
                return ErrorJson(tool, $"Unsupported pack format: {format}  Supported formats: cpio, boot, ext4, erofs.  不支持的打包格式：{format}  支持：cpio/boot/ext4/erofs。");
            }

            var result = resolved switch
            {
                "cpio" => await FirmwareService.Instance.PackCpioAsync(source, outputPath, ct).ConfigureAwait(false),
                "boot" => await FirmwareService.Instance.PackBootAsync(source, outputPath, ct).ConfigureAwait(false),
                "ext4" => await PackWithBuilderAsync(
                    () => Ext4Builder.Build(source, outputPath, new Ext4Options()), source, outputPath, "EXT4", ct).ConfigureAwait(false),
                "erofs" => await PackWithBuilderAsync(
                    () => new ErofsBuilder().Build(source, outputPath, new ErofsOptions()), source, outputPath, "EROFS", ct).ConfigureAwait(false),
                _ => FirmwareOpResult.Fail($"Unsupported pack format: {resolved}  不支持的打包格式：{resolved}"),
            };

            if (!result.Success)
            {
                return ErrorJson(tool, result.Summary);
            }

            var size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;
            return new JsonObject
            {
                ["success"] = true,
                ["tool"] = tool,
                ["format"] = resolved,
                ["inputDir"] = source,
                ["outputPath"] = outputPath,
                ["sizeBytes"] = size,
                ["summary"] = result.Summary,
            }.ToJsonString();
        }
        catch (OperationCanceledException)
        {
            return ErrorJson(tool, "Pack cancelled. 打包已取消。");
        }
        catch (Exception ex)
        {
            return ErrorJson(tool, ex.Message);
        }
    }

    /// <summary>
    /// <para>把镜像刷写到设备分区（ADB/Fastboot/EDL/RKDL 协议按设备模式自动选择）。
    /// 设备按 <paramref name="serial"/> 从 <see cref="DeviceMonitorService"/> 的当前扫描结果选取；
    /// 未指定 serial 时仅当恰好一台设备才刷写，多台/零台设备返回清晰错误。整体刷写设有 15 分钟超时，
    /// 所有异常被捕获并转为结构化 JSON 错误，不抛未捕获异常。</para>
    /// Flashes an image to a device partition (the protocol — ADB/Fastboot/EDL/RKDL — is picked from
    /// the device mode). The device is selected by <paramref name="serial"/> from the current
    /// <see cref="DeviceMonitorService"/> scan; without a serial, flashing proceeds only when exactly
    /// one device is present — zero or multiple devices yield a clear error. The whole flash runs
    /// under a 15-minute timeout; every failure is converted into structured JSON rather than an
    /// uncaught exception.
    /// </summary>
    /// <param name="imagePath">镜像文件完整路径。<para>Full path of the image file.</para></param>
    /// <param name="partition">目标分区名（如 boot/system/vendor）。<para>Target partition name (e.g. boot/system/vendor).</para></param>
    /// <param name="serial">
    /// <para>可选设备序列号；缺省时仅当扫描到恰好一台设备才执行。</para>
    /// Optional device serial; when omitted the flash runs only when exactly one device is detected.
    /// </param>
    /// <param name="ct">取消令牌。<para>Cancellation token.</para></param>
    /// <returns>结构化 JSON 字符串（success/tool/serial/protocol/partition/imagePath/summary/error）。</returns>
    [McpServerTool(Name = "flash.image"), Description("Flashes a firmware image to a device partition (ADB/Fastboot/EDL/RKDL) selected by serial (or the sole detected device) and returns the result as structured JSON.")]
    public async Task<string> FlashImage(
        string imagePath,
        string partition,
        string? serial = null,
        CancellationToken ct = default)
    {
        const string tool = "flash.image";
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return ErrorJson(tool, $"Image file not found: {imagePath}  镜像文件不存在：{imagePath}");
            }

            if (string.IsNullOrWhiteSpace(partition))
            {
                return ErrorJson(tool, "Partition name must not be empty. 分区名不能为空。");
            }

            var devices = DeviceMonitorService.Instance.Scan();
            DetectedDevice? target;
            if (!string.IsNullOrWhiteSpace(serial))
            {
                target = devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    return ErrorJson(tool, $"Device with serial '{serial}' not found. Detected: {FormatSerials(devices)}  未找到序列号为 '{serial}' 的设备。当前检测到：{FormatSerials(devices)}");
                }
            }
            else if (devices.Count == 1)
            {
                target = devices[0];
            }
            else if (devices.Count == 0)
            {
                return ErrorJson(tool, "No flashable device detected. 未检测到可刷写设备。");
            }
            else
            {
                return ErrorJson(tool, $"Multiple devices detected; specify 'serial'. Detected: {FormatSerials(devices)}  检测到多台设备，请指定 serial。当前检测到：{FormatSerials(devices)}");
            }

            var device = new DeviceInfo(target);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15)); // 长镜像/慢传输预留充分时间。

            var progress = new Progress<FlashProgress>(p =>
                LogService.Instance.Write(LogLevel.Info, "McpTools", $"flash.image [{p.Partition ?? partition}] {p.Percent}% {p.Status}"));
            var result = await FlashService.Default.FlashImageAsync(device, partition, imagePath, progress, timeout.Token).ConfigureAwait(false);

            var json = new JsonObject
            {
                ["success"] = result.Success,
                ["tool"] = tool,
                ["serial"] = target.Serial,
                ["protocol"] = target.Protocol.ToString(),
                ["partition"] = partition,
                ["imagePath"] = imagePath,
                ["summary"] = result.Summary,
            };
            if (!result.Success)
            {
                json["error"] = result.Summary;
            }

            return json.ToJsonString();
        }
        catch (OperationCanceledException)
        {
            return ErrorJson(tool, "Flash timed out or was cancelled. 刷写超时或已取消。");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "McpTools", $"flash.image failed: {ex.Message}");
            return ErrorJson(tool, ex.Message);
        }
    }

    /// <summary>
    /// <para>按格式提示（名称/扩展名/别名）或自动探测解析格式处理器。</para>
    /// Resolves the format handler from a format hint (name/extension/alias) or auto-detection.
    /// </summary>
    private static IFormatHandler? ResolveFormatHandler(string? format, string filePath)
    {
        FirmwareFormatRegistry.RegisterAll();
        if (string.IsNullOrWhiteSpace(format))
        {
            return FormatDetector.Detect(filePath);
        }

        var key = format.Trim().TrimStart('.').ToLowerInvariant();
        foreach (var handler in FormatDetector.RegisteredHandlers)
        {
            if (handler.FormatName.Equals(format, StringComparison.OrdinalIgnoreCase))
            {
                return handler;
            }

            foreach (var ext in handler.FileExtensions)
            {
                if (ext.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return handler;
                }
            }
        }

        var canonical = key switch
        {
            "boot" or "bootimg" or "vendor_boot" or "vendorboot" => "Android Boot / Vendor Boot Image",
            "cpio" or "tar" or "cpiogz" => "CPIO/TAR Archive",
            "payload" or "payloadbin" or "payload.bin" or "ota" => "Android OTA Payload (payload.bin)",
            "oppo" or "ofp" or "ops" or "ozip" => "Oppo Firmware (OFP/OPS/OZIP)",
            "super" or "lp" or "superimage" => "Android Logical Partition (super)",
            _ => null,
        };
        if (canonical is null)
        {
            return null;
        }

        foreach (var handler in FormatDetector.RegisteredHandlers)
        {
            if (handler.FormatName == canonical)
            {
                return handler;
            }
        }

        return null;
    }

    /// <summary>
    /// <para>按格式名把解包操作分派到 <see cref="FirmwareService"/> 的解包方法；
    /// 与 FirmwareInfoViewModel.DispatchUnpackAsync 的分派方式保持一致。</para>
    /// Dispatches the unpack operation to a <see cref="FirmwareService"/> unpack method by format
    /// name, mirroring FirmwareInfoViewModel.DispatchUnpackAsync.
    /// </summary>
    private static Task<FirmwareOpResult> DispatchUnpackAsync(
        IFormatHandler handler, string filePath, string outputDirectory, string[]? extractFiles, CancellationToken ct)
    {
        var service = FirmwareService.Instance;
        return handler.FormatName switch
        {
            "Android Boot / Vendor Boot Image" => service.UnpackBootAsync(filePath, outputDirectory, ct),
            "CPIO/TAR Archive" => service.UnpackCpioAsync(filePath, outputDirectory),
            "Android OTA Payload (payload.bin)" => service.ExtractPayloadAsync(filePath, outputDirectory, FirstNonEmpty(extractFiles)),
            "Oppo Firmware (OFP/OPS/OZIP)" => service.ExtractOppoAsync(filePath, outputDirectory, overwrite: true, ct),
            "Android Logical Partition (super)" => service.UnpackSuperAsync(filePath, outputDirectory, ct),
            // 文件系统镜像（ext4/erofs/f2fs）：通过对应流式解析器级联解包到目录。
            // Filesystem images (ext4/erofs/f2fs): cascade-unpack into the directory via the matching streaming parser.
            "EXT4" or "EROFS" or "F2FS" => service.UnpackFileSystemImageAsync(filePath, outputDirectory, ct),
            _ => Task.FromResult(FirmwareOpResult.Fail($"Unpack is not supported for format: {handler.FormatName}  不支持解包该格式：{handler.FormatName}")),
        };
    }

    /// <summary>
    /// <para>解析打包格式：显式格式校验，或按输出扩展名与源内容推断（cpio/boot/ext4/erofs）。</para>
    /// Resolves the pack format: validates the explicit format, or infers cpio/boot/ext4/erofs from
    /// the output extension and source content.
    /// </summary>
    private static string? ResolvePackFormat(string? format, string sourceDir, string outputPath)
    {
        if (!string.IsNullOrWhiteSpace(format))
        {
            var f = format.Trim().ToLowerInvariant();
            return f is "cpio" or "boot" or "ext4" or "erofs" ? f : null;
        }

        var hasHeaderJson = File.Exists(Path.Combine(sourceDir, "header.json"));
        var ext = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cpio" => "cpio",
            "erofs" => "erofs",
            "ext4" => "ext4",
            "img" => hasHeaderJson ? "boot" : "cpio",
            _ => hasHeaderJson ? "boot" : "cpio",
        };
    }

    /// <summary>
    /// <para>在后台线程执行同步镜像构建器（EXT4/EROFS），异常转为失败结果。</para>
    /// Runs a synchronous image builder (EXT4/EROFS) on the thread pool, converting exceptions into a
    /// failure result.
    /// </summary>
    private static Task<FirmwareOpResult> PackWithBuilderAsync(
        Action build, string sourceDir, string outputPath, string formatName, CancellationToken ct)
    {
        return Task.Run<FirmwareOpResult>(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                build();
                return new FirmwareOpResult
                {
                    Success = true,
                    Summary = $"Packed '{sourceDir}' as {formatName} image to '{outputPath}'  已将 '{sourceDir}' 打包为 {formatName} 镜像：{outputPath}",
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to pack {formatName} image: {ex.Message}  打包 {formatName} 镜像失败：{ex.Message}");
            }
        }, ct);
    }

    /// <summary>
    /// <para>取 <paramref name="items"/> 中首个非空白项；无则返回 <c>null</c>。</para>
    /// Returns the first non-empty entry of <paramref name="items"/>, or <c>null</c> when absent.
    /// </summary>
    private static string? FirstNonEmpty(string[]? items)
    {
        if (items is null)
        {
            return null;
        }

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// <para>把设备序列号列表拼接为逗号分隔文本（空列表返回 "none"）。</para>
    /// Joins the device serials into comma-separated text ("none" for an empty list).
    /// </summary>
    private static string FormatSerials(IReadOnlyList<DetectedDevice> devices)
    {
        if (devices is null || devices.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", devices.Select(static d => d.Serial));
    }

    /// <summary>
    /// <para>构造失败结果 JSON（success=false + tool + error）。</para>
    /// Builds the failure-result JSON (success=false + tool + error).
    /// </summary>
    private static string ErrorJson(string tool, string error)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["tool"] = tool,
            ["error"] = error,
        }.ToJsonString();
    }
}
