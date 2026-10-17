using System.IO.Compression;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.AndroidMeta.Arsc;
using UotanStudio.Firmware.AndroidMeta.AXml;
using UotanStudio.Firmware.AndroidMeta.IO;

namespace UotanStudio.Firmware.AndroidMeta.Apk;

/// <summary>
/// Reads an APK container and extracts its manifest metadata, detected signature schemes, packaged ABIs,
/// hardening markers and application icon into an <see cref="ApkInfo"/>.
/// <para>读取 APK 容器，把清单元数据、签名方案探测结果、打包 ABI、加固标记与应用图标提取到 <see cref="ApkInfo"/>。</para>
/// </summary>
public static class ApkParser
{
    /// <summary>
    /// Analyzes an APK file and extracts its metadata.
    /// <para>分析 APK 文件并提取元数据。</para>
    /// </summary>
    /// <param name="apkPath">Path to the APK on disk; the file must exist and is opened for reading only. <para>磁盘上的 APK 路径；文件必须存在且仅以只读方式打开。</para></param>
    /// <param name="preferredLanguage">Locale for the localized application label, for example <c>en-US</c>; <see langword="null"/> keeps the default configuration. <para>本地化应用名称所用的语言，例如 <c>en-US</c>；为 <see langword="null"/> 时取默认配置。</para></param>
    /// <returns>A populated <see cref="ApkInfo"/>; unresolvable fields keep their empty or zero defaults. <para>填充完成的 <see cref="ApkInfo"/>；无法解析的字段保持空值或零默认。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="apkPath"/> is <see langword="null"/>. <para><paramref name="apkPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="apkPath"/>. <para><paramref name="apkPath"/> 处没有文件。</para></exception>
    /// <exception cref="InvalidDataException">The ZIP is malformed, or a manifest, icon or resources entry exceeds its read limit. <para>ZIP 结构非法，或清单、图标、resources 条目超出读取上限。</para></exception>
    /// <exception cref="AXmlException">The embedded binary-XML manifest cannot be parsed. <para>内嵌的二进制 XML 清单无法解析。</para></exception>
    /// <exception cref="ArscException">The embedded resource table cannot be parsed. <para>内嵌的资源表无法解析。</para></exception>
    /// <example>
    /// <code language="csharp">
    /// ApkInfo info = ApkParser.Analyze(apkPath);
    /// </code>
    /// <para>分析指定路径的 APK 文件。</para>
    /// </example>
    public static ApkInfo Analyze(string apkPath, string? preferredLanguage = null)
        => AnalyzeCore(apkPath, preferredLanguage, includeResourceMetadata: true);

    /// <summary>
    /// Reads only the manifest metadata, entry names, signature schemes and hardening markers of an
    /// APK, skipping the resource table and icon reads that unpack does not consume.
    /// <para>只读取 APK 的清单元数据、条目名、签名方案与加固标记，跳过解包不会消费的资源表与图标读取。</para>
    /// </summary>
    /// <param name="apkPath">Path to the APK on disk. <para>磁盘上的 APK 路径。</para></param>
    /// <returns>A populated <see cref="ApkInfo"/> whose label and icon fields stay unresolved.
    /// <para>已填充的 <see cref="ApkInfo"/>；其标签与图标字段保持未解析状态。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="apkPath"/> is <see langword="null"/>. <para><paramref name="apkPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="apkPath"/>. <para><paramref name="apkPath"/> 处没有文件。</para></exception>
    /// <exception cref="InvalidDataException">The ZIP is malformed, or the manifest entry exceeds its read limit. <para>ZIP 结构非法，或清单条目超出读取上限。</para></exception>
    /// <exception cref="AXmlException">The embedded binary-XML manifest cannot be parsed. <para>内嵌的二进制 XML 清单无法解析。</para></exception>
    internal static ApkInfo AnalyzeForUnpack(string apkPath)
        => AnalyzeCore(apkPath, preferredLanguage: null, includeResourceMetadata: false);

    private static ApkInfo AnalyzeCore(string apkPath, string? preferredLanguage, bool includeResourceMetadata)
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

        DetectSignatures(zip, apkPath, info);
        info.HardeningProvider = DetectHardening(zip);


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

        if (!includeResourceMetadata)
        {
            return info;
        }

        var arscEntry = zip.GetEntry("resources.arsc");
        if (arscEntry is not null)
        {
            var table = ArscParser.Parse(ReadMetadata(arscEntry, AndroidMetadataReader.MaximumBytes));
            if (info.ApplicationLabelReference != 0
                && (LookupLocalizedString(table, info.ApplicationLabelReference, preferredLanguage)
                    ?? LookupEntryPath(table, info.ApplicationLabelReference)) is { } applicationName)
            {
                info.ApplicationName = applicationName;
            }
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

    private static void DetectSignatures(ZipArchive zip, string apkPath, ApkInfo info)
    {
        var v1Manifest = zip.Entries.Any(e => string.Equals(e.FullName, "META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
        var v1Signature = zip.Entries.Any(e => e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
            && (e.FullName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)));
        if (v1Manifest && v1Signature) info.SigningSchemes.Add("v1");

        try
        {
            using var stream = new FileStream(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.SequentialScan);
            var sections = ApkZipSections.Locate(stream);
            if (ApkSigningBlock.Find(stream, sections) is not { } found) return;
            foreach (var (id, _) in ApkSigningBlock.ParsePairs(found.Contents))
            {
                if (id == ApkSignatureConstants.V2BlockId && !info.SigningSchemes.Contains("v2")) info.SigningSchemes.Add("v2");
                if (id == ApkSignatureConstants.V3BlockId && !info.SigningSchemes.Contains("v3")) info.SigningSchemes.Add("v3");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or EndOfStreamException or ArgumentOutOfRangeException)
        {
        }
        info.SigningSchemes.Sort(StringComparer.Ordinal);
    }

    private static string? DetectHardening(ZipArchive zip)
    {
        var markers = new (string Marker, string Provider)[]
        {
            ("libjiagu", "360 Jiagu"), ("libsecexe", "SecNeo"), ("libshell", "Bangcle"),
            ("libdexprotector", "DexProtector"), ("libDexHelper", "Tencent Legu"),
            ("com.secneo.apkwrapper", "SecNeo"), ("com.tencent.StubShell", "Tencent Legu"),
            ("com.baidu.protect", "Baidu Jiagu"), ("libstub", "Stub shell")
        };
        foreach (var entry in zip.Entries)
        {
            foreach (var (marker, provider) in markers)
                if (entry.FullName.Contains(marker, StringComparison.OrdinalIgnoreCase)) return provider;
        }
        return null;
    }

    /// <summary>
    /// Parses the manifest root and children: package name, version, SDK, permissions and icon reference.
    /// <para>解析 manifest 根元素与子元素：包名、版本、SDK、权限与图标引用。</para>
    /// </summary>
    /// <param name="doc">Parsed manifest document whose element tree is read. <para>待读取元素树的已解析清单文档。</para></param>
    /// <param name="info">Container filled with the manifest values found. <para>写入所发现清单值的容器。</para></param>
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
        info.ApplicationName = Attr("label") ?? string.Empty;
        info.VersionName = Attr("versionName") ?? string.Empty;
        info.VersionCode = ParseLong(Attr("versionCode"));


        info.MinSdkVersion = ParseLong(Attr("minSdkVersion") ?? UsesSdkAttr("minSdkVersion"));
        info.TargetSdkVersion = ParseLong(Attr("targetSdkVersion") ?? UsesSdkAttr("targetSdkVersion"));
        info.CompileSdkVersion = ParseLong(Attr("compileSdkVersion") ?? UsesSdkAttr("compileSdkVersion"));


        void Walk(AXmlElement el)
        {
            if (string.Equals(el.Name, "application", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(info.ApplicationName))
            {
                var label = el.Attributes.FirstOrDefault(a => string.Equals(a.Name, "label", StringComparison.Ordinal));
                if (label is not null)
                {
                    info.ApplicationName = label.FormatValue(doc.StringPool, doc.ResourceMap);
                    if (label.ValueType is AXmlValueType.Reference or AXmlValueType.DynamicReference)
                    {
                        info.ApplicationLabelReference = label.ValueData;
                    }
                }
            }
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
    /// <param name="icon">Icon attribute whose value data may hold a resource reference. <para>值数据可能为资源引用的图标属性。</para></param>
    /// <returns>The referenced resource ID, or 0 when the attribute is not reference-typed. <para>被引用的资源 ID；属性不是引用类型时为 0。</para></returns>
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
    /// <param name="table">Parsed resource table searched for the referenced entry. <para>用于查找被引用条目的已解析资源表。</para></param>
    /// <param name="zip">Archive used to confirm the resolved path is present. <para>用于确认解析出的路径确实存在的归档。</para></param>
    /// <param name="iconReference">Resource ID taken from the manifest icon attribute. <para>取自清单 icon 属性的资源 ID。</para></param>
    /// <returns>The bitmap entry path, or <see langword="null"/> when no entry matches. <para>位图条目路径；无匹配条目时为 <see langword="null"/>。</para></returns>
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
    /// <param name="table">Parsed resource table used to resolve the references. <para>用于解析资源引用的已解析资源表。</para></param>
    /// <param name="zip">Archive holding the icon description entries. <para>存放图标描述条目的归档。</para></param>
    /// <param name="xmlPath">Path of the icon description to follow. <para>待跟随的图标描述路径。</para></param>
    /// <param name="visited">Paths already followed, guarding against reference cycles. <para>已跟随的路径，用于防止引用成环。</para></param>
    /// <returns>The bitmap path reached through the references, or <see langword="null"/> when none resolves. <para>沿引用到达的位图路径；无法解析时为 <see langword="null"/>。</para></returns>
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
    /// <param name="el">Element whose attributes and descendants are scanned. <para>待扫描属性与子孙的元素。</para></param>
    /// <param name="references">List receiving the collected resource IDs. <para>接收所收集资源 ID 的列表。</para></param>
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
    /// <param name="path">Entry path to classify. <para>待归类的条目路径。</para></param>
    /// <returns><see langword="true"/> when the path ends with a bitmap extension. <para>路径以位图扩展名结尾时为 <see langword="true"/>。</para></returns>
    private static bool IsImagePath(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Locates an entry by resource ID in ARSC and returns its string value, following reference entries.
    /// <para>按资源 ID 在 ARSC 中定位条目并返回其字符串值，会跟随引用型条目。</para>
    /// </summary>
    /// <param name="table">Parsed resource table to search. <para>待查找的已解析资源表。</para></param>
    /// <param name="resId">Resource ID built from package, type and entry index. <para>由包、类型与条目索引组成的资源 ID。</para></param>
    /// <param name="visited">IDs already followed, guarding against reference cycles. <para>已跟随的资源 ID，用于防止引用成环。</para></param>
    /// <returns>The resolved string value, or <see langword="null"/> when the ID does not resolve. <para>解析出的字符串值；该 ID 无法解析时为 <see langword="null"/>。</para></returns>
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

    private static string? LookupLocalizedString(ResourceTable table, uint resId, string? preferredLanguage,
        HashSet<uint>? visited = null)
    {
        visited ??= [];
        if (resId == 0 || visited.Count >= 64 || !visited.Add(resId)) return null;

        var pkgId = (byte)(resId >> 24);
        var typeId = (byte)((resId >> 16) & 0xff);
        var entryIndex = (int)(resId & 0xffff);
        var candidates = new List<(ResValue Value, string Config)>();
        foreach (var pkg in table.Packages)
        {
            if (pkg.Id != pkgId) continue;
            foreach (var type in pkg.Types)
            {
                if (type.Id + pkg.TypeIdOffset != typeId || entryIndex >= type.Entries.Count
                    || type.Entries[entryIndex] is not { Value: { } value } || value.IsComplex) continue;
                candidates.Add((value, type.ConfigDescription));
            }
        }

        var requested = NormalizeLocale(preferredLanguage);
        var selected = candidates
            .OrderByDescending(candidate => LocaleScore(NormalizeLocale(candidate.Config), requested))
            .FirstOrDefault();
        if (selected.Value is null) return null;
        return selected.Value.Type switch
        {
            ResValueType.String => table.GlobalPool.GetString(checked((int)selected.Value.Data)),
            ResValueType.Reference or ResValueType.DynamicReference =>
                LookupLocalizedString(table, selected.Value.Data, preferredLanguage, visited),
            _ => null
        };
    }

    private static int LocaleScore(string config, string requested)
    {
        if (config.Length == 0 || config == "default") return 0;
        if (requested.Length == 0) return 0;
        if (string.Equals(config, requested, StringComparison.OrdinalIgnoreCase)) return 300;
        var requestedLanguage = requested.Split('-')[0];
        return string.Equals(config.Split('-')[0], requestedLanguage, StringComparison.OrdinalIgnoreCase) ? 200 : 0;
    }

    private static string NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return string.Empty;
        var value = locale.Trim().Replace('_', '-');
        return value.Replace("-r", "-", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    }

    /// <summary>
    /// Fallback: scans all resource types for image paths, preferring entries whose name contains "launcher" or "icon".
    /// <para>兜底：扫描所有资源类型，优先返回资源名含 launcher/icon 的图片路径。</para>
    /// </summary>
    /// <param name="table">Parsed resource table whose entries are scanned. <para>待扫描条目的已解析资源表。</para></param>
    /// <returns>The preferred image path, or <see langword="null"/> when none is found. <para>优先选用的图片路径；未找到时为 <see langword="null"/>。</para></returns>
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