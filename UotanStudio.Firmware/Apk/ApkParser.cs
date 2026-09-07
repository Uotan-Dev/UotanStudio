using System.IO.Compression;
using UotanStudio.Firmware.Arsc;
using UotanStudio.Firmware.AXml;

namespace UotanStudio.Firmware.Apk;

public static class ApkParser
{
    /// <summary>
    /// Analyzes an APK file and extracts its metadata.
    /// <para>分析 APK 文件并提取元数据。</para>
    /// </summary>
    public static ApkInfo Analyze(string apkPath)
    {
        ArgumentNullException.ThrowIfNull(apkPath);
        var info = new ApkInfo { FilePath = apkPath };

        var fileInfo = new FileInfo(apkPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("APK file not found", apkPath);
        }
        info.FileSize = fileInfo.Length;

        using var zip = ZipFile.OpenRead(apkPath);
        info.EntryCount = zip.Entries.Count;


        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = entry.FullName.Split('/');
                if (parts.Length >= 2 && parts[1].Length > 0 && !info.Abis.Contains(parts[1]))
                {
                    info.Abis.Add(parts[1]);
                }
            }
        }
        info.Abis.Sort(StringComparer.Ordinal);


        var manifestEntry = zip.GetEntry("AndroidManifest.xml");
        if (manifestEntry is not null)
        {
            var doc = AXmlParser.Parse(ReadMetadata(manifestEntry, 16 * 1024 * 1024));
            ExtractManifest(doc, info);
        }


        var arscEntry = zip.GetEntry("resources.arsc");
        if (arscEntry is not null)
        {
            var table = ArscParser.Parse(ReadMetadata(arscEntry, AndroidMetadataReader.MaximumBytes));
            info.IconPath ??= ResolveIconPath(table, zip, info.IconReference);
        }


        if (info.IconPath is not null && info.IconBytes is null)
        {
            var iconEntry = zip.GetEntry(info.IconPath);
            if (iconEntry is not null)
            {
                info.IconBytes = ReadMetadata(iconEntry, 16 * 1024 * 1024);
            }
        }


        info.IconReference = 0;

        return info;
    }

    /// <summary>
    /// Parses the manifest root and children: package name, version, SDK, permissions and icon reference.
    /// <para>解析 manifest 根元素与子元素：包名、版本、SDK、权限与图标引用。</para>
    /// </summary>
    private static void ExtractManifest(AXmlDocument doc, ApkInfo info)
    {
        var root = doc.Root;
        string? Attr(string name)
        {
            var attr = root.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
            return attr?.FormatValue(doc.StringPool, doc.ResourceMap);
        }


        string? UsesSdkAttr(string name)
        {
            AXmlElement? FindUsesSdk(AXmlElement el)
            {
                foreach (var child in el.Children)
                {
                    if (string.Equals(child.Name, "uses-sdk", StringComparison.Ordinal))
                    {
                        return child;
                    }
                    if (FindUsesSdk(child) is { } found)
                    {
                        return found;
                    }
                }
                return null;
            }

            var usesSdk = FindUsesSdk(root);
            var attr = usesSdk?.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
            return attr?.FormatValue(doc.StringPool, doc.ResourceMap);
        }

        info.PackageName = Attr("package") ?? string.Empty;
        info.VersionName = Attr("versionName") ?? string.Empty;
        info.VersionCode = ParseLong(Attr("versionCode"));


        info.MinSdkVersion = ParseLong(Attr("minSdkVersion") ?? UsesSdkAttr("minSdkVersion"));
        info.TargetSdkVersion = ParseLong(Attr("targetSdkVersion") ?? UsesSdkAttr("targetSdkVersion"));
        info.CompileSdkVersion = ParseLong(Attr("compileSdkVersion") ?? UsesSdkAttr("compileSdkVersion"));


        void Walk(AXmlElement el)
        {
            if (string.Equals(el.Name, "uses-permission", StringComparison.Ordinal))
            {
                var name = el.Attributes.FirstOrDefault(a => string.Equals(a.Name, "name", StringComparison.Ordinal));
                var value = name?.FormatValue(doc.StringPool, doc.ResourceMap);
                if (!string.IsNullOrEmpty(value) && !info.Permissions.Contains(value))
                {
                    info.Permissions.Add(value);
                }
            }

            if (info.IconReference == 0)
            {
                var icon = el.Attributes.FirstOrDefault(a => string.Equals(a.Name, "icon", StringComparison.Ordinal));
                if (icon is not null)
                {
                    info.IconReference = ResolveIconReference(icon);
                }
            }

            foreach (var child in el.Children)
            {
                Walk(child);
            }
        }

        foreach (var child in root.Children)
        {
            Walk(child);
        }

        info.Permissions.Sort(StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves the icon AXML attribute to a resource ID from the Reference-typed value data field.
    /// <para>把图标的 AXML 属性的 Reference 型 data 字段解析为资源 ID。</para>
    /// </summary>
    private static uint ResolveIconReference(AXmlAttribute icon)
    {
        if (icon.ValueType is AXmlValueType.Reference or AXmlValueType.DynamicReference)
        {
            return icon.ValueData;
        }

        return 0;
    }

    /// <summary>
    /// Resolves the icon file path in ARSC by resource ID; falls back to scanning image entries.
    /// <para>在 ARSC 中按资源 ID 查图标路径；失败时兜底扫描图片条目。</para>
    /// </summary>
    private static string? ResolveIconPath(ResourceTable table, ZipArchive zip, uint iconReference)
    {


        var path = iconReference != 0 ? LookupEntryPath(table, iconReference) : null;
        if (path is null && iconReference == 0)
        {
            path = ScanForImagePath(table);
        }


        if (path is not null && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            path = ResolveXmlIconPath(table, zip, path) ?? path;
        }

        return path is not null && zip.GetEntry(path) is not null ? path : null;
    }

    /// <summary>
    /// Recursively resolves an adaptive/layer-list icon XML to a bitmap path by following resource references.
    /// <para>递归解析自适应/图层图标 XML，沿资源引用链找到位图路径。</para>
    /// </summary>
    private static string? ResolveXmlIconPath(ResourceTable table, ZipArchive zip, string xmlPath, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (visited.Count >= 64 || !visited.Add(xmlPath))
        {
            return null;
        }

        var entry = zip.GetEntry(xmlPath);
        if (entry is null)
        {
            return null;
        }

        var bytes = ReadMetadata(entry, 16 * 1024 * 1024);


        if (bytes.Length < 4 || bytes[0] != 0x03)
        {
            return null;
        }

        AXmlDocument doc;
        try
        {
            doc = AXmlParser.Parse(bytes);
        }
        catch
        {
            return null;
        }


        if (string.Equals(doc.Root.Name, "vector", StringComparison.Ordinal))
        {
            return xmlPath;
        }

        var references = new List<uint>();
        CollectReferences(doc.Root, references);
        foreach (var refId in references)
        {
            var resolved = LookupEntryPath(table, refId);
            if (resolved is null)
            {
                continue;
            }

            if (IsImagePath(resolved) && zip.GetEntry(resolved) is not null)
            {
                return resolved;
            }


            if (resolved.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var nested = ResolveXmlIconPath(table, zip, resolved, visited);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Recursively collects resource IDs of every Reference/DynamicReference attribute in the element tree.
    /// <para>递归收集元素树中所有资源引用属性值。</para>
    /// </summary>
    private static void CollectReferences(AXmlElement el, List<uint> references)
    {
        foreach (var attr in el.Attributes)
        {
            if (attr.ValueType is AXmlValueType.Reference or AXmlValueType.DynamicReference && attr.ValueData != 0)
            {
                references.Add(attr.ValueData);
            }
        }

        foreach (var child in el.Children)
        {
            CollectReferences(child, references);
        }
    }

    /// <summary>
    /// Determines whether the path has a renderable bitmap extension (PNG/WebP/JPG).
    /// <para>判断路径是否为可渲染位图扩展名（PNG/WebP/JPG）。</para>
    /// </summary>
    private static bool IsImagePath(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Locates an entry by resource ID (0xPPTTEEEE) in ARSC and returns its string value; references are resolved one level.
    /// <para>按资源 ID 在 ARSC 中定位条目并返回其字符串值；引用型条目递归解析一层。</para>
    /// </summary>
    private static string? LookupEntryPath(ResourceTable table, uint resId, HashSet<uint>? visited = null)
    {
        visited ??= [];
        if (visited.Count >= 64 || !visited.Add(resId)) return null;
        var pkgId = (byte)(resId >> 24);
        var typeId = (byte)((resId >> 16) & 0xff);
        var entryIndex = (int)(resId & 0xffff);

        foreach (var pkg in table.Packages)
        {
            if (pkg.Id != pkgId)
            {
                continue;
            }

            foreach (var type in pkg.Types)
            {
                if (type.Id + pkg.TypeIdOffset != typeId)
                {
                    continue;
                }

                if (entryIndex >= type.Entries.Count || type.Entries[entryIndex] is not { } entry
                    || entry.Value is { IsComplex: true })
                {
                    continue;
                }

                var value = entry.Value!;
                if (value.Type == ResValueType.String)
                {
                    return table.GlobalPool.GetString(checked((int)value.Data));
                }


                if (value.Type is ResValueType.Reference or ResValueType.DynamicReference
                    && value.Data != 0 && value.Data != resId)
                {
                    return LookupEntryPath(table, value.Data, visited);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Fallback: scans all resource types for image paths, preferring entries whose name contains "launcher" or "icon".
    /// <para>兜底：扫描所有资源类型，优先返回资源名含 launcher/icon 的图片路径。</para>
    /// </summary>
    private static string? ScanForImagePath(ResourceTable table)
    {
        string? fallback = null;
        foreach (var pkg in table.Packages)
        {
            foreach (var type in pkg.Types)
            {
                foreach (var entry in type.Entries)
                {
                    if (entry is null || entry.Value is { IsComplex: true } || entry.Value?.Type != ResValueType.String)
                    {
                        continue;
                    }

                    var path = table.GlobalPool.GetString(checked((int)entry.Value.Data));
                    if (!IsImagePath(path))
                    {
                        continue;
                    }

                    if (fallback is null)
                    {
                        fallback = path;
                    }


                    var key = pkg.KeyStrings.GetString(entry.KeyIndex);
                    if (key.Contains("launcher", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("icon", StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }
        }
        return fallback;
    }

    private static byte[] ReadMetadata(ZipArchiveEntry entry, int maximumBytes)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes)
            throw new InvalidDataException($"APK entry '{entry.FullName}' exceeds the {maximumBytes}-byte metadata limit.");
        using var stream = entry.Open();
        return AndroidMetadataReader.Read(stream, maximumBytes, entry.Length);
    }

    private static long ParseLong(string? text)
    {
        if (text is null)
        {
            return 0;
        }
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var hex) ? hex : 0;
        }
        return long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var dec) ? dec : 0;
    }
}