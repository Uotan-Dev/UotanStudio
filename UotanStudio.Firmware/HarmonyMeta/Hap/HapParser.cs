using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.AndroidMeta.IO;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Reads HarmonyOS application containers (<c>.hap</c>/<c>.hsp</c> modules and <c>.app</c> bundles) and extracts
/// bundle identity, version, device support, permissions, abilities, signing information and an application icon
/// into a <see cref="HapInfo"/>.
/// <para>读取 HarmonyOS 应用容器（<c>.hap</c>/<c>.hsp</c> 模块与 <c>.app</c> 包），把应用标识、版本、设备支持、
/// 权限、Ability、签名信息与应用图标提取到 <see cref="HapInfo"/>。</para>
/// </summary>
/// <remarks>
/// Every metadata read is bounded: the container, each JSON document, the application signature block and the icon
/// have independent limits. Malformed optional metadata is skipped rather than failing the whole analysis, so a
/// container can still be shown in the info window when only one of its metadata sections is damaged.
/// <para>每项元数据读取都有独立上限：容器、各 JSON 文档、应用签名块与图标分别受限。可选元数据结构损坏时跳过该项，
/// 不让整个分析失败，因此某一节元数据损坏时仍可在信息窗口中展示容器。</para>
/// </remarks>
public static class HapParser
{
    private const long MaximumContainerBytes = 4L << 30;
    private const int MaximumManifestBytes = 4 << 20;
    private const int MaximumModuleBytes = 128 << 20;
    private const int MaximumIconBytes = 16 << 20;
    private const int MaximumSignatureScanBytes = 1 << 20;
    private const int MaximumJsonBytes = 4 << 20;

    /// <summary>Analyzes a HarmonyOS application container and extracts its metadata.<para>分析 HarmonyOS 应用容器并提取元数据。</para></summary>
    /// <param name="path">Path to the container on disk; the file is opened for reading only.
    /// <para>磁盘上的容器路径；文件仅以只读方式打开。</para></param>
    /// <param name="preferredLanguage">Reserved for future resource-qualifier selection; currently unused.
    /// <para>为将来的资源限定符选择保留；当前未使用。</para></param>
    /// <returns>A populated <see cref="HapInfo"/>; unresolvable fields keep their empty or zero defaults.
    /// <para>填充完成的 <see cref="HapInfo"/>；无法解析的字段保持空值或零默认。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty or whitespace.
    /// <para><paramref name="path"/> 为 <see langword="null"/>、空或空白。</para></exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.
    /// <para><paramref name="path"/> 处没有文件。</para></exception>
    /// <exception cref="InvalidDataException">The container is not a supported ZIP32 archive, or a metadata read
    /// exceeds its limit. <para>容器不是受支持的 ZIP32 归档，或某项元数据读取超出上限。</para></exception>
    /// <example>
    /// <code language="csharp">
    /// HapInfo info = HapParser.Analyze(appPath);
    /// </code>
    /// <para>分析指定路径的 HAP/APP 文件。</para>
    /// </example>
    public static HapInfo Analyze(string path, string? preferredLanguage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("HarmonyOS application container not found", path);
        }
        if (fileInfo.Length > MaximumContainerBytes)
        {
            throw new InvalidDataException(
                $"HarmonyOS container exceeds the {MaximumContainerBytes}-byte limit. HarmonyOS 容器超过 {MaximumContainerBytes} 字节上限。");
        }

        var info = new HapInfo
        {
            FilePath = path,
            FileSize = fileInfo.Length,
            ContainerKind = HasBundleExtension(path) ? HapContainerKind.Bundle : HapContainerKind.Module,
        };

        using var zip = ZipContainer.OpenRead(path);
        info.EntryCount = zip.Entries.Count;

        ReadAppSignature(path, info);

        var packEntry = FindEntry(zip, "pack.info");
        if (packEntry is not null && packEntry.Length is > 0 and <= MaximumManifestBytes)
        {
            SafeJson(ReadEntry(packEntry, MaximumManifestBytes), document => ReadPackInfo(document, info));
            info.ContainerKind = HapContainerKind.Bundle;
        }

        if (info.ContainerKind == HapContainerKind.Bundle)
        {
            ReadBundleModules(zip, info);
            info.IsHapSigned = DetectHapSignatureInBundle(zip);
        }
        else
        {
            ReadModuleFrom(zip, info);
            info.IsHapSigned = DetectHapSignatureInContainer(path);
            ReadIcon(zip, info);
        }

        return info;
    }

    /// <summary>
    /// Reports whether a path is a HarmonyOS application container that this parser can describe.
    /// <para>报告路径是否为该解析器可以描述的 HarmonyOS 应用容器。</para>
    /// </summary>
    /// <param name="path">Candidate file path. <para>候选文件路径。</para></param>
    /// <returns><see langword="true"/> when the extension and archive metadata identify a HAP, HSP or APP package.
    /// <para>扩展名与归档元数据共同标识 HAP、HSP 或 APP 包时为 <see langword="true"/>。</para></returns>
    public static bool IsApplicationContainer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !HasApplicationExtension(path)
            || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var zip = ZipContainer.OpenRead(path);
            return FindEntry(zip, "pack.info") is not null || FindEntry(zip, "module.json") is not null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasApplicationExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".hap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".hsp", StringComparison.OrdinalIgnoreCase)
            || HasBundleExtension(path);
    }

    /// <summary>Reports whether the extension identifies an APP bundle rather than a single module.<para>报告扩展名是否标识 APP 包而非单个模块。</para></summary>
    /// <param name="path">Container path. <para>容器路径。</para></param>
    /// <returns><see langword="true"/> for <c>.app</c>. <para>扩展名为 <c>.app</c> 时为 <see langword="true"/>。</para></returns>
    private static bool HasBundleExtension(string path)
        => string.Equals(Path.GetExtension(path), ".app", StringComparison.OrdinalIgnoreCase);

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
        => zip.Entries.FirstOrDefault(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase));

    private static byte[] ReadEntry(ZipArchiveEntry entry, int maximumBytes)
    {
        if (entry.Length > maximumBytes)
        {
            throw new InvalidDataException($"Container entry '{entry.FullName}' exceeds its read limit. 容器条目 '{entry.FullName}' 超出读取上限。");
        }
        using var stream = entry.Open();
        return AndroidMetadataReader.Read(stream, maximumBytes, entry.Length);
    }

    private static void SafeJson(byte[] bytes, Action<JsonElement> consume)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            consume(document.RootElement);
        }
        catch (JsonException)
        {
        }
    }

    private static void ReadPackInfo(JsonElement root, HapInfo info)
    {
        if (!root.TryGetProperty("summary", out var summary))
        {
            return;
        }

        if (summary.TryGetProperty("app", out var app))
        {
            info.PackageName = GetString(app, "bundleName", info.PackageName);
            info.BundleType = GetString(app, "bundleType", info.BundleType);
            if (app.TryGetProperty("version", out var version))
            {
                info.VersionName = GetString(version, "name", info.VersionName);
                info.VersionCode = GetInt64(version, "code", info.VersionCode);
            }
        }

        if (!summary.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var module in modules.EnumerateArray())
        {
            AddStrings(module, "deviceType", info.DeviceTypes);
            AddNamedObjects(module, "abilities", info.Abilities);
            AddNamedObjects(module, "extensionAbilities", info.ExtensionAbilities);

            if (module.TryGetProperty("apiVersion", out var api))
            {
                info.TargetApiVersion = GetInt64(api, "target", info.TargetApiVersion);
                info.CompatibleApiVersion = GetInt64(api, "compatible", info.CompatibleApiVersion);
                info.ApiReleaseType = GetString(api, "releaseType", info.ApiReleaseType);
            }
        }
    }

    private static void ReadBundleModules(ZipArchive zip, HapInfo info)
    {
        foreach (var entry in zip.Entries)
        {
            var extension = Path.GetExtension(entry.FullName);
            if (entry.Length is <= 0 or > MaximumModuleBytes
                || !string.Equals(extension, ".hap", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".hsp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] moduleBytes;
            try
            {
                moduleBytes = ReadEntry(entry, MaximumModuleBytes);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            using var stream = new MemoryStream(moduleBytes, writable: false);
            using var moduleZip = ZipContainer.OpenRead(stream, leaveOpen: true);
            ReadModuleFrom(moduleZip, info);
            ReadIcon(moduleZip, info);
        }
    }

    private static void ReadModuleFrom(ZipArchive zip, HapInfo info)
    {
        var moduleEntry = FindEntry(zip, "module.json");
        if (moduleEntry is null || moduleEntry.Length is <= 0 or > MaximumManifestBytes)
        {
            return;
        }

        SafeJson(ReadEntry(moduleEntry, MaximumManifestBytes), root =>
        {
            if (root.TryGetProperty("app", out var app))
            {
                info.PackageName = GetString(app, "bundleName", info.PackageName);
                info.ApplicationName = GetString(app, "label", info.ApplicationName);
                info.VersionName = GetString(app, "versionName", info.VersionName);
                info.VersionCode = GetInt64(app, "versionCode", info.VersionCode);
                info.BundleType = GetString(app, "bundleType", info.BundleType);
                info.Vendor = GetString(app, "vendor", info.Vendor);
                info.ApiReleaseType = GetString(app, "apiReleaseType", info.ApiReleaseType);
                info.TargetApiVersion = GetInt64(app, "targetAPIVersion", info.TargetApiVersion);
                info.CompatibleApiVersion = GetInt64(app, "minAPIVersion", info.CompatibleApiVersion);
                info.CompileSdkVersion = GetString(app, "compileSdkVersion", info.CompileSdkVersion);
            }

            if (!root.TryGetProperty("module", out var module))
            {
                return;
            }

            AddStrings(module, "deviceTypes", info.DeviceTypes);
            AddNamedObjects(module, "abilities", info.Abilities);
            AddNamedObjects(module, "extensionAbilities", info.ExtensionAbilities);
            if (module.TryGetProperty("requestPermissions", out var permissions)
                && permissions.ValueKind == JsonValueKind.Array)
            {
                foreach (var permission in permissions.EnumerateArray())
                {
                    var name = GetString(permission, "name", string.Empty);
                    if (name.Length > 0 && !info.Permissions.Contains(name))
                    {
                        info.Permissions.Add(name);
                    }
                }
            }
        });

        info.DeviceTypes.Sort(StringComparer.Ordinal);
        info.Permissions.Sort(StringComparer.Ordinal);
        info.Abilities.Sort(StringComparer.Ordinal);
        info.ExtensionAbilities.Sort(StringComparer.Ordinal);
    }

    private static void ReadIcon(ZipArchive zip, HapInfo info)
    {
        if (info.IconBytes is not null)
        {
            return;
        }

        var candidates = new[] { "resources/base/media/startIcon.png", "resources/base/media/foreground.png", "resources/base/media/background.png" };
        foreach (var candidate in candidates)
        {
            var entry = FindEntry(zip, candidate);
            if (entry is null || entry.Length is <= 0 or > MaximumIconBytes)
            {
                continue;
            }
            try
            {
                info.IconPath = candidate;
                info.IconBytes = ReadEntry(entry, MaximumIconBytes);
                return;
            }
            catch (InvalidDataException)
            {
                info.IconPath = null;
            }
        }

        var fallback = zip.Entries.FirstOrDefault(entry =>
            entry.Length is > 0 and <= MaximumIconBytes
            && entry.FullName.StartsWith("resources/base/media/", StringComparison.OrdinalIgnoreCase)
            && entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        if (fallback is null)
        {
            return;
        }
        try
        {
            info.IconPath = fallback.FullName;
            info.IconBytes = ReadEntry(fallback, MaximumIconBytes);
        }
        catch (InvalidDataException)
        {
            info.IconPath = null;
        }
    }

    private static bool DetectHapSignatureInContainer(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var sections = ApkZipSections.Locate(stream);
            return HapSigningBlock.Find(stream, sections.CentralDirectoryOffset) is not null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool DetectHapSignatureInBundle(ZipArchive zip)
    {
        foreach (var entry in zip.Entries)
        {
            if (entry.Length is <= 0 or > MaximumModuleBytes
                || !string.Equals(Path.GetExtension(entry.FullName), ".hap", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                var bytes = ReadEntry(entry, MaximumModuleBytes);
                using var stream = new MemoryStream(bytes, writable: false);
                var sections = ApkZipSections.Locate(stream);
                if (HapSigningBlock.Find(stream, sections.CentralDirectoryOffset) is not null)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
            {
            }
        }
        return false;
    }

    private static void ReadAppSignature(string path, HapInfo info)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var sections = ApkZipSections.Locate(stream);
            var scanStart = Math.Max(0, sections.CentralDirectoryOffset - MaximumSignatureScanBytes);
            var scanLength = sections.CentralDirectoryOffset - scanStart;
            if (scanLength < 4)
            {
                return;
            }

            stream.Seek(scanStart, SeekOrigin.Begin);
            var window = new byte[scanLength];
            stream.ReadExactly(window);

            for (var index = window.Length - 4; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(window.AsSpan(index, 4)) != 0x08074b50)
                {
                    continue;
                }

                var cms = FindCms(window, index + 4, window.Length);
                if (cms is null)
                {
                    continue;
                }

                info.IsAppSigned = true;
                try
                {
                    var profile = HapProfile.Parse(cms);
                    info.SignatureType = profile.Type;
                    info.DeveloperId = profile.DeveloperId;
                    info.Issuer = profile.Issuer;
                    (info.ValidityNotBefore, info.ValidityNotAfter) = profile.Validity;
                }
                catch (InvalidDataException)
                {
                }
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
        }
    }

    private static byte[]? FindCms(byte[] window, int start, int end)
    {
        var limit = Math.Min(end, start + 128);
        for (var index = start; index + 4 <= limit; index++)
        {
            if (window[index] != 0x30 || window[index + 1] != 0x82)
            {
                continue;
            }
            if (index + 14 > end
                || window[index + 4] != 0x06 || window[index + 5] != 0x09
                || window[index + 6] != 0x2a || window[index + 7] != 0x86
                || window[index + 8] != 0x48 || window[index + 9] != 0x86
                || window[index + 10] != 0xf7 || window[index + 11] != 0x0d
                || window[index + 12] != 0x01 || window[index + 13] != 0x07)
            {
                continue;
            }

            var contentLength = BinaryPrimitives.ReadUInt16BigEndian(window.AsSpan(index + 2, 2));
            var totalLength = 4 + contentLength;
            if (totalLength <= 0 || index + totalLength > end || totalLength > MaximumJsonBytes * 2)
            {
                return null;
            }
            return window.AsSpan(index, totalLength).ToArray();
        }
        return null;
    }

    private static string GetString(JsonElement parent, string name, string fallback)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static long GetInt64(JsonElement parent, string name, long fallback)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : fallback;

    private static void AddStrings(JsonElement parent, string name, List<string> destination)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrEmpty(value) && !destination.Contains(value))
                {
                    destination.Add(value);
                }
            }
        }
    }

    private static void AddNamedObjects(JsonElement parent, string name, List<string> destination)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var item in array.EnumerateArray())
        {
            var value = GetString(item, "name", string.Empty);
            if (value.Length > 0 && !destination.Contains(value))
            {
                destination.Add(value);
            }
        }
    }
}