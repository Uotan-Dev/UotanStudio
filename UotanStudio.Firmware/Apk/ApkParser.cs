using System.IO.Compression;
using UotanStudio.Firmware.Arsc;
using UotanStudio.Firmware.AXml;

namespace UotanStudio.Firmware.Apk;

/// <summary>
/// <para>APK 元数据解析器：读取 APK（zip 容器）清单，经 AXML 解析 AndroidManifest.xml
/// （包名/版本/SDK/权限/图标引用），经 ARSC 解析 resources.arsc 查表解析图标路径，
/// 枚举 lib/ 原生库 ABI 并统计文件大小与条目数。纯托管实现，无反射。</para>
/// APK metadata parser: reads the APK (zip container) manifest, parses AndroidManifest.xml via AXML
/// (package name/version/SDK/permissions/icon reference), resolves the icon path via the ARSC
/// resource table, enumerates lib/ native ABIs, and reports file size and entry counts. Fully
/// managed, no reflection.
/// </summary>
public static class ApkParser
{
    /// <summary>
    /// <para>分析 APK 文件并提取元数据。</para>
    /// Analyzes an APK file and extracts its metadata.
    /// </summary>
    /// <param name="apkPath">APK 文件路径。<para>APK file path.</para></param>
    /// <returns>提取的元数据。<para>The extracted metadata.</para></returns>
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

        // lib/<abi>/xxx.so → ABI 列表（按目录名去重）。
        // lib/<abi>/xxx.so → ABI list (deduplicated by directory name).
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

        // AndroidManifest.xml（AXML）→ 包名/版本/SDK/权限/图标引用。
        // AndroidManifest.xml (AXML) → package/version/SDK/permissions/icon reference.
        var manifestEntry = zip.GetEntry("AndroidManifest.xml");
        if (manifestEntry is not null)
        {
            using var ms = new MemoryStream();
            using (var entryStream = manifestEntry.Open())
            {
                entryStream.CopyTo(ms);
            }
            var doc = AXmlParser.Parse(ms.ToArray());
            ExtractManifest(doc, info);
        }

        // resources.arsc → 图标路径查表（图标引用 + 兜底扫描）。
        // resources.arsc → icon path resolution (icon reference + fallback scan).
        var arscEntry = zip.GetEntry("resources.arsc");
        if (arscEntry is not null)
        {
            using var ms = new MemoryStream();
            using (var entryStream = arscEntry.Open())
            {
                entryStream.CopyTo(ms);
            }
            var table = ArscParser.Parse(ms.ToArray());
            info.IconPath ??= ResolveIconPath(table, zip, info.IconReference);
        }

        // 读取图标字节（PNG/WebP/JPG/XML 原样读出，UI 按扩展名渲染或占位）。
        // Read the icon bytes verbatim (PNG/WebP/JPG/XML; the UI renders or falls back by extension).
        if (info.IconPath is not null && info.IconBytes is null)
        {
            var iconEntry = zip.GetEntry(info.IconPath);
            if (iconEntry is not null)
            {
                using var iconStream = iconEntry.Open();
                using var iconMs = new MemoryStream();
                iconStream.CopyTo(iconMs);
                info.IconBytes = iconMs.ToArray();
            }
        }

        // 图标引用仅用于查表，不对外展示。
        // The icon reference is only used for lookup, not exposed.
        info.IconReference = 0;

        return info;
    }

    /// <summary>
    /// <para>解析 manifest 根元素与子元素：包名、版本、SDK、权限与图标引用。</para>
    /// Parses the manifest root and children: package name, version, SDK, permissions and icon reference.
    /// </summary>
    private static void ExtractManifest(AXmlDocument doc, ApkInfo info)
    {
        var root = doc.Root;
        string? Attr(string name)
        {
            var attr = root.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
            return attr?.FormatValue(doc.StringPool, doc.ResourceMap);
        }

        // minSdkVersion/targetSdkVersion 位于 <uses-sdk> 子元素而非根元素，需递归查找；
        // compileSdkVersion 同样可能出现在 uses-sdk 上。
        // minSdkVersion/targetSdkVersion live on the <uses-sdk> child element, not the root, so walk
        // the tree for them; compileSdkVersion may appear there too.
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

        // 根元素未提供时回退到 <uses-sdk> 子元素。
        // Fall back to the <uses-sdk> child when the root attribute is absent.
        info.MinSdkVersion = ParseLong(Attr("minSdkVersion") ?? UsesSdkAttr("minSdkVersion"));
        info.TargetSdkVersion = ParseLong(Attr("targetSdkVersion") ?? UsesSdkAttr("targetSdkVersion"));
        info.CompileSdkVersion = ParseLong(Attr("compileSdkVersion") ?? UsesSdkAttr("compileSdkVersion"));

        // 权限与图标：递归遍历元素树。
        // Permissions and icon: walk the element tree recursively.
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
    /// <para>把图标的 AXML 属性解析为资源 ID：Reference 型属性值的 data 字段即资源 ID
    /// （0xPPTTEEEE），无需经过 ResourceMap（该映射仅用于属性名识别）。</para>
    /// Resolves the icon AXML attribute to a resource ID: for Reference-typed values the data field
    /// already carries the resource ID (0xPPTTEEEE); the ResourceMap only maps attribute names.
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
    /// <para>在 ARSC 中按资源 ID 查图标文件路径；失败时兜底扫描 drawable/mipmap 的图片条目。
    /// 命中的路径若为自适应图标 XML（<c>mipmap-anydpi-v26/ic_launcher.xml</c> 等），
    /// 递归解析其中引用的位图资源。</para>
    /// Resolves the icon file path in ARSC by resource ID; falls back to scanning drawable/mipmap
    /// image entries when the ID lookup fails. A resolved adaptive-icon XML (e.g.
    /// <c>mipmap-anydpi-v26/ic_launcher.xml</c>) is recursively resolved to the bitmap it references.
    /// </summary>
    private static string? ResolveIconPath(ResourceTable table, ZipArchive zip, uint iconReference)
    {
        var path = iconReference != 0 ? LookupEntryPath(table, iconReference) : null;
        path ??= ScanForImagePath(table);

        if (path is not null && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            path = ResolveXmlIconPath(table, zip, path) ?? ScanForImagePath(table);
        }

        return path is not null && zip.GetEntry(path) is not null ? path : null;
    }

    /// <summary>
    /// <para>解析自适应图标 XML（AXML）：收集其中所有资源引用属性
    /// （<c>background</c>/<c>foreground</c>/<c>drawable</c> 等），逐个查表返回首个位图路径。</para>
    /// Resolves an adaptive-icon XML (AXML): collects every resource-reference attribute
    /// (<c>background</c>/<c>foreground</c>/<c>drawable</c> etc.) and returns the first bitmap path
    /// found by lookup.
    /// </summary>
    private static string? ResolveXmlIconPath(ResourceTable table, ZipArchive zip, string xmlPath)
    {
        var entry = zip.GetEntry(xmlPath);
        if (entry is null)
        {
            return null;
        }

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using var entryStream = entry.Open();
            entryStream.CopyTo(ms);
            bytes = ms.ToArray();
        }

        // AXML 魔数 03 00 08 00（ResXmlType）。
        // AXML magic 03 00 08 00 (ResXmlType).
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

        var references = new List<uint>();
        CollectReferences(doc.Root, references);
        foreach (var refId in references)
        {
            var resolved = LookupEntryPath(table, refId);
            if (resolved is not null && IsImagePath(resolved) && zip.GetEntry(resolved) is not null)
            {
                return resolved;
            }
        }
        return null;
    }

    /// <summary>
    /// <para>递归收集元素树中所有资源引用属性值（Reference/DynamicReference 型）。</para>
    /// Recursively collects the resource IDs of every Reference/DynamicReference attribute in the
    /// element tree.
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
    /// <para>判断路径是否为可渲染位图扩展名（PNG/WebP/JPG）。</para>
    /// Determines whether the path has a renderable bitmap extension (PNG/WebP/JPG).
    /// </summary>
    private static bool IsImagePath(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>按资源 ID（0xPPTTEEEE）在 ARSC 中定位条目并返回其字符串值（文件路径）；
    /// 引用型条目递归解析一层。</para>
    /// Locates the entry by resource ID (0xPPTTEEEE) in ARSC and returns its string value (file
    /// path); Reference-typed entries are resolved one level recursively.
    /// </summary>
    private static string? LookupEntryPath(ResourceTable table, uint resId)
    {
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
                if (type.Id != typeId)
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

                // 引用型：指向另一条目（如 tint/selector 场景），递归一层。
                // Reference: points at another entry (tint/selector cases); resolve one level.
                if (value.Type is ResValueType.Reference or ResValueType.DynamicReference
                    && value.Data != 0 && value.Data != resId)
                {
                    return LookupEntryPath(table, value.Data);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// <para>兜底：扫描所有资源类型的条目，返回第一个含图片扩展名的文件路径。
    /// 不限定 drawable/mipmap 类型编号——实际 APK 的图标资源类型编号不固定
    /// （Magisk 等位于 type 8），扩展名过滤已足够精确。</para>
    /// Fallback: scans entries of every resource type, returning the first path with an image
    /// extension. Not limited to drawable/mipmap type IDs — real APKs place icon resources under
    /// varying type IDs (Magisk uses type 8), and the extension filter is precise enough.
    /// </summary>
    private static string? ScanForImagePath(ResourceTable table)
    {
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
                    if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }
        }
        return null;
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