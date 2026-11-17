using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Services;

/// <summary>Registers and removes file associations for the supported formats on Windows, macOS and Linux.<para>在 Windows、macOS 与 Linux 上注册和移除受支持格式的文件关联。</para></summary>
/// <remarks>
/// The three built-in project formats are always associated. Additional extensions come from the persisted
/// <c>AssociatedFileExtensions</c> setting so the check boxes in Settings have a real, cross-platform effect.
/// All OS writes are best effort: a missing platform helper only leaves a warning in the log and never blocks startup.
/// <para>三个内置工程格式始终关联；其余扩展名来自持久化的 <c>AssociatedFileExtensions</c> 设置，因此设置页的勾选会在三个平台真正生效。
/// 所有系统写入均为尽力而为：缺少平台辅助工具只记警告，不阻断启动。</para>
/// </remarks>
public static partial class FileAssociationService
{
    private const string ClassesRoot = @"Software\Classes";
    private const string MacBundleIdentifier = "club.openatom.uotanstudio.desktop";
    private const string DesktopEntryId = "uotanstudio";
    private const int RegSz = 1;
    private const int RegOptionNonVolatile = 0;
    private const int KeyWrite = 0x00020006;
    private const int KeyRead = 0x00020019;

    private static readonly nint Hkcu = new(0x80000001L);

    private static readonly (string Extension, string ProgId, string IconFile, string Description)[] Formats =
    [
        (".uotan", "UotanStudio.uotan", "uotan.ico", "UotanStudio Solution"),
        (".uotanfw", "UotanStudio.uotanfw", "uotanfw.ico", "UotanStudio Firmware Project"),
        (".uotannode", "UotanStudio.uotannode", "uotannode.ico", "UotanStudio Node Project")
    ];

    /// <summary>
    /// Extensions that Project Explorer can automatically recognize when opening files
    /// (excluding generic code/text and image formats).
    /// <para>项目资源管理器在打开文件时可自动识别的固件、镜像、Android、归档与日志类扩展名。</para>
    /// </summary>
    public static IReadOnlyList<string> RecognizedExtensions { get; } =
    [
        // Firmware, partition, filesystem, device-tree & firmware compression
        ".img", ".bin", ".dat", ".lz4", ".br", ".dtb", ".dtbo", ".dts", ".dtsi",
        ".ext4", ".erofs", ".f2fs", ".qcn",
        ".pac", ".ops", ".ofp", ".ozip", ".nb0", ".dz", ".kdz", ".app", ".sin",
        ".mpk", ".cpb", ".ntpi",
        // Android / HarmonyOS app & system metadata/config
        ".apk", ".hap", ".dex", ".arsc", ".prop", ".rc",
        // Device logs
        ".logcat"
    ];

    /// <summary>
    /// Extensions checked by default in the file-association settings UI.
    /// <para>在文件关联设置界面中默认勾选的扩展名列表。</para>
    /// </summary>
    public static IReadOnlyList<string> DefaultAssociatedExtensions { get; } =
    [
        ".img", ".br", ".dtb", ".dtbo", ".dts", ".dtsi", ".qcn",
        ".pac", ".ops", ".ofp", ".ozip", ".nb0", ".dz", ".kdz",
        ".app", ".sin", ".mpk", ".cpb", ".ntpi", ".logcat"
    ];

    [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int RegCreateKeyExW(nint hKey, string lpSubKey, int reserved, string? lpClass, int dwOptions, int samDesired, nint lpSecurityAttributes, out nint phkResult, out int lpdwDisposition);

    [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int RegSetValueExW(nint hKey, string? lpValueName, int reserved, int dwType, byte[] lpData, int cbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int RegOpenKeyExW(nint hKey, string lpSubKey, int ulOptions, int samDesired, out nint phkResult);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int RegQueryValueExW(nint hKey, string? lpValueName, int lpReserved, out int lpType, byte[]? lpData, ref int lpcbData);

    [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteTreeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int RegDeleteTreeW(nint hKey, string lpSubKey);

    [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey", SetLastError = true)]
    private static partial int RegCloseKey(nint hKey);

    /// <summary>
    /// Registers the built-in project formats together with <paramref name="selectedExtensions"/> and reports
    /// a structured outcome, classifying permission failures so the caller can offer elevation.
    /// <para>幂等注册内置工程格式以及 <paramref name="selectedExtensions"/> 指定的扩展名，并返回结构化结果；
    /// 权限不足会被单独分类，便于调用方提供提权选项。</para>
    /// </summary>
    /// <param name="selectedExtensions">Optional user selection; null registers only the built-in formats.
    /// <para>用户勾选的扩展名；为 null 时只注册内置格式。</para></param>
    /// <returns>The detailed registration outcome.<para>详细的注册结果。</para></returns>
    public static FileAssociationResult RegisterDetailed(IReadOnlyList<string>? selectedExtensions = null)
    {
        var specs = BuildSpecs(selectedExtensions);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return RegisterWindows(specs)
                    ? FileAssociationResult.Succeeded(specs.Count)
                    : FileAssociationResult.Failed("Windows registration did not complete.");
            }

            if (OperatingSystem.IsMacOS())
            {
                return RegisterMacOS(specs)
                    ? FileAssociationResult.Succeeded(specs.Count)
                    : FileAssociationResult.Failed("macOS registration did not complete.");
            }

            if (OperatingSystem.IsLinux())
            {
                return RegisterLinux(specs)
                    ? FileAssociationResult.Succeeded(specs.Count)
                    : FileAssociationResult.Failed("Linux registration did not complete.");
            }

            return FileAssociationResult.Unsupported();
        }
        catch (Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            LogService.Instance.Write(LogLevel.Error, "FileAssociation", $"Register failed: {message}");
            return IsPermissionFailure(ex)
                ? FileAssociationResult.ElevationRequired(message)
                : FileAssociationResult.Failed(message);
        }
    }

    /// <summary>
    /// Registers the built-in formats with the persisted selection and returns only whether it succeeded.
    /// <para>使用持久化选择注册内置格式，仅返回是否成功。</para>
    /// </summary>
    /// <param name="selectedExtensions">Optional user selection.<para>用户勾选的扩展名。</para></param>
    /// <returns>True on success.<para>成功时为真。</para></returns>
    public static bool Register(IReadOnlyList<string>? selectedExtensions = null) =>
        RegisterDetailed(selectedExtensions).Status == FileAssociationStatus.Succeeded;

    /// <summary>
    /// Removes the built-in project formats and every extension in <paramref name="selectedExtensions"/>.
    /// <para>移除内置工程格式以及 <paramref name="selectedExtensions"/> 中的全部扩展名。</para>
    /// </summary>
    /// <param name="selectedExtensions">Optional user selection; null removes only the built-in formats.
    /// <para>用户勾选的扩展名；为 null 时只移除内置格式。</para></param>
    /// <returns>True when the current platform completed unregistration.<para>当前平台完成注销时为真。</para></returns>
    public static bool Unregister(IReadOnlyList<string>? selectedExtensions = null)
    {
        var specs = BuildSpecs(selectedExtensions);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return UnregisterWindows(specs);
            }

            if (OperatingSystem.IsMacOS())
            {
                return UnregisterMacOS(specs);
            }

            if (OperatingSystem.IsLinux())
            {
                return UnregisterLinux(specs);
            }

            return false;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "FileAssociation", $"Unregister failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reports whether an exception is an OS permission failure that elevation could resolve.
    /// <para>报告异常是否属于提权可能解决的系统权限失败。</para>
    /// </summary>
    /// <param name="ex">The exception to classify.<para>待分类的异常。</para></param>
    /// <returns>True when the failure is an access-denied error.<para>属于拒绝访问错误时为真。</para></returns>
    internal static bool IsPermissionFailure(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return true;
        }

        return ex is System.ComponentModel.Win32Exception win32 &&
            win32.NativeErrorCode is 5 or 740;
    }

    /// <summary>
    /// Merges the built-in project formats with the requested extensions into a normalized, deduplicated spec list.
    /// <para>把内置工程格式与请求的扩展名合并为规范化、去重的规格列表。</para>
    /// </summary>
    /// <param name="selectedExtensions">Requested extensions, already normalized by the settings layer.
    /// <para>请求的扩展名，设置层已完成规范化。</para></param>
    /// <returns>The association specs to apply.<para>待应用的关联规格。</para></returns>
    internal static IReadOnlyList<AssociationSpec> BuildSpecs(IReadOnlyList<string>? selectedExtensions)
    {
        var result = new List<AssociationSpec>(Formats.Length + (selectedExtensions?.Count ?? 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (extension, progId, iconFile, description) in Formats)
        {
            if (seen.Add(extension))
            {
                result.Add(new AssociationSpec(extension, progId, iconFile, description));
            }
        }

        if (selectedExtensions is not null)
        {
            foreach (var raw in selectedExtensions)
            {
                var extension = NormalizeExtension(raw);
                if (extension.Length == 0 || !seen.Add(extension))
                {
                    continue;
                }

                result.Add(new AssociationSpec(
                    extension,
                    ProgIdFor(extension),
                    Formats[0].IconFile,
                    $"UotanStudio {extension[1..].ToUpperInvariant()} File"));
            }
        }

        return result;
    }

    /// <summary>Normalizes a user-selected extension to a lowercase dotted form.<para>把用户选择的扩展名规范化为小写带点形式。</para></summary>
    /// <param name="extension">Raw extension.<para>原始扩展名。</para></param>
    /// <returns>Normalized extension, or an empty string when invalid.<para>规范化后的扩展名；无效时为空字符串。</para></returns>
    internal static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var text = extension.Trim();
        if (text[0] != '.')
        {
            text = "." + text;
        }

        return text.Any(char.IsWhiteSpace) ? string.Empty : text.ToLowerInvariant();
    }

    /// <summary>Derives a stable Windows ProgId from an extension.<para>从扩展名派生稳定的 Windows ProgId。</para></summary>
    /// <param name="extension">Normalized extension.<para>规范化后的扩展名。</para></param>
    /// <returns>The ProgId used for the extension.<para>该扩展名使用的 ProgId。</para></returns>
    internal static string ProgIdFor(string extension) =>
        $"UotanStudio.{extension.TrimStart('.').ToLowerInvariant()}";

    /// <summary>Builds the XDG shared-MIME XML document for the given specs.<para>为给定规格生成 XDG 共享 MIME XML 文档。</para></summary>
    /// <param name="specs">Association specs.<para>关联规格。</para></param>
    /// <returns>The XML document content.<para>XML 文档内容。</para></returns>
    internal static string BuildMimeXml(IReadOnlyList<AssociationSpec> specs)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">\n");
        foreach (var spec in specs)
        {
            var mime = MimeTypeFor(spec.Extension);
            var escaped = EscapeXml(mime);
            builder.Append($"  <mime-type type=\"{escaped}\">\n");
            builder.Append($"    <comment>{EscapeXml(spec.Description)}</comment>\n");
            builder.Append($"    <glob pattern=\"*{EscapeXml(spec.Extension)}\"/>\n");
            builder.Append("  </mime-type>\n");
        }

        builder.Append("</mime-info>\n");
        return builder.ToString();
    }

    /// <summary>Builds the Linux desktop entry content listing every associated MIME type.<para>生成列出全部关联 MIME 类型的 Linux 桌面项内容。</para></summary>
    /// <param name="specs">Association specs.<para>关联规格。</para></param>
    /// <param name="executable">Absolute path to the application executable.<para>应用可执行文件的绝对路径。</para></param>
    /// <returns>The desktop entry content.<para>桌面项内容。</para></returns>
    internal static string BuildDesktopEntry(IReadOnlyList<AssociationSpec> specs, string executable)
    {
        var mimeTypes = string.Join(';', specs.Select(spec => MimeTypeFor(spec.Extension))) + ";";
        return string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=UotanStudio",
            "Comment=Android firmware, flashing and device tools",
            $"Exec={EscapeDesktopValue(executable)} %f",
            "Icon=uotanstudio",
            "Terminal=false",
            "Categories=Development;Utility;",
            $"MimeType={mimeTypes}",
            string.Empty);
    }

    /// <summary>Builds the macOS document-type declarations used by duti and the generated import plist.<para>生成 duti 与导入 plist 使用的 macOS 文档类型声明。</para></summary>
    /// <param name="specs">Association specs.<para>关联规格。</para></param>
    /// <returns>One declaration per spec, in the same order.<para>与规格顺序一致的声明集合。</para></returns>
    internal static IReadOnlyList<MacDocumentType> BuildMacDocumentTypes(IReadOnlyList<AssociationSpec> specs) =>
        [.. specs.Select(spec => new MacDocumentType(spec.Extension, MacBundleIdentifier))];

    /// <summary>Derives an XDG MIME type from a file extension.<para>从文件扩展名派生 XDG MIME 类型。</para></summary>
    /// <param name="extension">Normalized extension.<para>规范化后的扩展名。</para></param>
    /// <returns>The MIME type string.<para>MIME 类型字符串。</para></returns>
    internal static string MimeTypeFor(string extension)
    {
        var suffix = NormalizeExtension(extension).TrimStart('.');
        return $"application/x-uotanstudio-{suffix}";
    }

    private static bool RegisterWindows(IReadOnlyList<AssociationSpec> specs)
    {
        if (OperatingSystem.IsWindows() && !RegisterWindowsCore(specs))
        {
            return false;
        }

        LogService.Instance.Write(LogLevel.Info, "FileAssociation",
            $"Registered {specs.Count} file association(s) on Windows.");
        return true;
    }

    private static bool RegisterWindowsCore(IReadOnlyList<AssociationSpec> specs)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            LogService.Instance.Write(LogLevel.Warn, "FileAssociation", "Cannot register associations: process path is empty.");
            return false;
        }

        var iconDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
        foreach (var spec in specs)
        {
            SetStringValue(Path.Combine(ClassesRoot, spec.ProgId), null, spec.Description);
            SetStringValue(Path.Combine(ClassesRoot, spec.ProgId, "DefaultIcon"), null,
                $"\"{Path.Combine(iconDirectory, spec.IconFile)}\",0");
            SetStringValue(Path.Combine(ClassesRoot, spec.ProgId, "shell", "open", "command"), null,
                $"\"{exePath}\" \"%1\"");
            SetStringValue(Path.Combine(ClassesRoot, spec.Extension), null, spec.ProgId);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterMacOS(IReadOnlyList<AssociationSpec> specs)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var directory = Path.Combine(AppPaths.Root, "FileAssociations");
        Directory.CreateDirectory(directory);
        var plistPath = Path.Combine(directory, "DocumentTypes.plist");
        WriteAtomically(plistPath, BuildMacImportPlist(specs));

        foreach (var type in BuildMacDocumentTypes(specs))
        {
            RunHelper("duti", $"-s {MacBundleIdentifier} {type.Extension} all", "macOS");
        }

        RefreshLaunchServices();
        LogService.Instance.Write(LogLevel.Info, "FileAssociation",
            $"Registered {specs.Count} file association(s) on macOS.");
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string BuildMacImportPlist(IReadOnlyList<AssociationSpec> specs)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        builder.Append("<plist version=\"1.0\"><dict><key>CFBundleDocumentTypes</key><array>\n");
        foreach (var type in BuildMacDocumentTypes(specs))
        {
            builder.Append("  <dict>\n");
            builder.Append($"    <key>CFBundleTypeExtensions</key><array><string>{EscapeXml(type.Extension.TrimStart('.'))}</string></array>\n");
            builder.Append($"    <key>CFBundleTypeName</key><string>{EscapeXml(type.Extension)} File</string>\n");
            builder.Append("    <key>CFBundleTypeRole</key><string>Viewer</string>\n");
            builder.Append("  </dict>\n");
        }

        builder.Append("</array></dict></plist>\n");
        return builder.ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RefreshLaunchServices()
    {
        var candidates = new[]
        {
            "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
            "/usr/bin/lsregister",
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                RunHelper(candidate, "-R -f", "macOS");
                return;
            }
        }

        LogService.Instance.Write(LogLevel.Warn, "FileAssociation",
            "LaunchServices refresh skipped: lsregister was not found.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool UnregisterMacOS(IReadOnlyList<AssociationSpec> specs)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var directory = Path.Combine(AppPaths.Root, "FileAssociations");
        TryDelete(Path.Combine(directory, "DocumentTypes.plist"));
        foreach (var type in BuildMacDocumentTypes(specs))
        {
            RunHelper("duti", $"-d {type.Extension} all", "macOS");
        }

        RefreshLaunchServices();
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterLinux(IReadOnlyList<AssociationSpec> specs)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        // File associations are OS integration points, not application data: the freedesktop spec only
        // scans the XDG data home, so these files must live there rather than under AppPaths.Root.
        // This is the same ecosystem-path exception the repository keeps for ~/.android and ~/.hdc.
        // <para>文件关联是系统集成分支点而非应用数据：freedesktop 规范只扫描 XDG 数据目录，
        // 因此这些文件必须放在那里而不是 AppPaths.Root 下，与 ~/.android、~/.hdc 的生态路径例外同类。</para>
        var dataHome = ResolveXdgDataHome();
        var mimeDirectory = Path.Combine(dataHome, "mime", "packages");
        var applicationsDirectory = Path.Combine(dataHome, "applications");
        Directory.CreateDirectory(mimeDirectory);
        Directory.CreateDirectory(applicationsDirectory);

        WriteAtomically(Path.Combine(mimeDirectory, "uotanstudio.xml"), BuildMimeXml(specs));
        var executable = Environment.ProcessPath ?? "UotanStudio.Desktop";
        var desktopPath = Path.Combine(applicationsDirectory, $"{DesktopEntryId}.desktop");
        WriteAtomically(desktopPath, BuildDesktopEntry(specs, executable));
        RefreshLinuxDatabases(dataHome);
        LogService.Instance.Write(LogLevel.Info, "FileAssociation",
            $"Registered {specs.Count} file association(s) on Linux.");
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool UnregisterLinux(IReadOnlyList<AssociationSpec> specs)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var dataHome = ResolveXdgDataHome();
        TryDelete(Path.Combine(dataHome, "mime", "packages", "uotanstudio.xml"));
        TryDelete(Path.Combine(dataHome, "applications", $"{DesktopEntryId}.desktop"));
        RefreshLinuxDatabases(dataHome);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RefreshLinuxDatabases(string dataHome)
    {
        RunHelper("update-mime-database", Path.Combine(dataHome, "mime"), "Linux");
        RunHelper("update-desktop-database", Path.Combine(dataHome, "applications"), "Linux");
    }

    /// <summary>Resolves the XDG data home used by freedesktop file associations.<para>解析 freedesktop 文件关联使用的 XDG 数据目录。</para></summary>
    /// <returns>The absolute XDG data home path.<para>XDG 数据目录的绝对路径。</para></returns>
    internal static string ResolveXdgDataHome()
    {
        var configured = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    private static bool UnregisterWindows(IReadOnlyList<AssociationSpec> specs)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var spec in specs)
        {
            RegDeleteTreeW(Hkcu, Path.Combine(ClassesRoot, spec.ProgId));
            if (GetStringValue(Path.Combine(ClassesRoot, spec.Extension), null) == spec.ProgId)
            {
                RegDeleteTreeW(Hkcu, Path.Combine(ClassesRoot, spec.Extension));
            }
        }

        LogService.Instance.Write(LogLevel.Info, "FileAssociation",
            $"Unregistered {specs.Count} file association(s) on Windows.");
        return true;
    }

    private static void SetStringValue(string subKey, string? valueName, string value)
    {
        var createResult = RegCreateKeyExW(Hkcu, subKey, 0, null, RegOptionNonVolatile, KeyWrite, nint.Zero, out var key, out _);
        if (createResult != 0)
        {
            throw new System.ComponentModel.Win32Exception(createResult,
                $"Failed to open/create registry key '{subKey}'.");
        }

        try
        {
            var data = Encoding.Unicode.GetBytes(value + "\0");
            RegSetValueExW(key, valueName, 0, RegSz, data, data.Length);
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static string? GetStringValue(string subKey, string? valueName)
    {
        if (RegOpenKeyExW(Hkcu, subKey, 0, KeyRead, out var key) != 0)
        {
            return null;
        }

        try
        {
            var size = 0;
            if (RegQueryValueExW(key, valueName, 0, out _, null, ref size) != 0 || size <= 0)
            {
                return null;
            }

            var data = new byte[size];
            if (RegQueryValueExW(key, valueName, 0, out _, data, ref size) != 0)
            {
                return null;
            }

            var text = Encoding.Unicode.GetString(data);
            var end = text.IndexOf('\0');
            return end >= 0 ? text[..end] : text;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Instance.Write(LogLevel.Warn, "FileAssociation",
                $"Deleting '{path}' failed: {ex.Message}");
        }
    }

    private static void RunHelper(string file, string arguments, string platform)
    {
        try
        {
            var startInfo = new ProcessStartInfo(file)
            {
                ArgumentList = { arguments },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                LogService.Instance.Write(LogLevel.Warn, "FileAssociation",
                    $"{platform} helper '{file}' did not start.");
                return;
            }

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LogService.Instance.Write(LogLevel.Warn, "FileAssociation",
                $"{platform} helper '{file}' unavailable: {ex.Message}");
        }
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    private static string EscapeDesktopValue(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Describes one file association to register on any platform.<para>描述要在任意平台注册的一条文件关联。</para></summary>
    /// <param name="Extension">Normalized extension with a leading dot.<para>带前导点的规范化扩展名。</para></param>
    /// <param name="ProgId">Windows ProgId.<para>Windows ProgId。</para></param>
    /// <param name="IconFile">Bundled icon file name.<para>随包图标文件名。</para></param>
    /// <param name="Description">Human-readable description.<para>可读描述。</para></param>
    internal sealed record AssociationSpec(string Extension, string ProgId, string IconFile, string Description);

    /// <summary>Describes a macOS document type declaration.<para>描述一条 macOS 文档类型声明。</para></summary>
    /// <param name="Extension">Normalized extension.<para>规范化后的扩展名。</para></param>
    /// <param name="BundleIdentifier">Target application bundle identifier.<para>目标应用包标识符。</para></param>
    internal sealed record MacDocumentType(string Extension, string BundleIdentifier);
}

/// <summary>Outcome status of a file-association registration attempt.<para>文件关联注册尝试的结果状态。</para></summary>
public enum FileAssociationStatus
{
    /// <summary>Registration completed successfully.<para>注册成功完成。</para></summary>
    Succeeded,

    /// <summary>Registration failed because the process lacks permission; elevation can resolve it.
    /// <para>因进程权限不足导致注册失败；提权可解决。</para></summary>
    ElevationRequired,

    /// <summary>Registration failed for a reason other than permission.<para>因权限以外的原因导致注册失败。</para></summary>
    Failed,

    /// <summary>The current operating system is not supported.<para>当前操作系统不受支持。</para></summary>
    Unsupported,
}

/// <summary>Detailed outcome of a file-association registration attempt.<para>文件关联注册尝试的详细结果。</para></summary>
/// <param name="Status">The outcome status.<para>结果状态。</para></param>
/// <param name="Message">A human-readable detail, especially the failure reason.<para>可读的详情，尤其是失败原因。</para></param>
/// <param name="AssociationCount">Number of formats registered on success.<para>成功时注册的关联数量。</para></param>
public sealed record FileAssociationResult(FileAssociationStatus Status, string Message, int AssociationCount)
{
    /// <summary>Creates a success result.<para>创建成功结果。</para></summary>
    /// <param name="count">Number of registered formats.<para>注册的关联数量。</para></param>
    /// <returns>The success result.<para>成功结果。</para></returns>
    public static FileAssociationResult Succeeded(int count) =>
        new(FileAssociationStatus.Succeeded, string.Empty, count);

    /// <summary>Creates an elevation-required failure result.<para>创建需要提权的失败结果。</para></summary>
    /// <param name="reason">The underlying permission failure.<para>底层的权限失败原因。</para></param>
    /// <returns>The result.<para>结果。</para></returns>
    public static FileAssociationResult ElevationRequired(string reason) =>
        new(FileAssociationStatus.ElevationRequired, reason, 0);

    /// <summary>Creates a general failure result.<para>创建一般失败结果。</para></summary>
    /// <param name="reason">The failure reason.<para>失败原因。</para></param>
    /// <returns>The result.<para>结果。</para></returns>
    public static FileAssociationResult Failed(string reason) =>
        new(FileAssociationStatus.Failed, reason, 0);

    /// <summary>Creates an unsupported-platform result.<para>创建平台不支持结果。</para></summary>
    /// <returns>The result.<para>结果。</para></returns>
    public static FileAssociationResult Unsupported() =>
        new(FileAssociationStatus.Unsupported, "File associations are not supported on this operating system.", 0);
}

/// <summary>Command-line contract of the elevated file-association helper.<para>文件关联提权助手的命令行契约。</para></summary>
public static class FileAssociationElevatedCommand
{
    /// <summary>Switch selecting the elevated registration helper.<para>选择提权注册助手的开关。</para></summary>
    public const string RegisterSwitch = "--file-associations-register";

    /// <summary>Switch carrying the file the helper writes its result to.<para>携带助手写入结果文件路径的开关。</para></summary>
    public const string ResultSwitch = "--file-associations-result";

    /// <summary>Gets whether the arguments select the elevated file-association helper.
    /// <para>获取参数是否选择文件关联提权助手。</para></summary>
    /// <param name="args">Process arguments.<para>进程参数。</para></param>
    /// <returns>True when the helper is requested.<para>请求助手时为真。</para></returns>
    public static bool IsRequested(string[] args) => args is [RegisterSwitch, ..];

    /// <summary>Finds the value following a named switch.<para>查找命名开关之后的值。</para></summary>
    /// <param name="args">Process arguments.<para>进程参数。</para></param>
    /// <param name="name">The switch name.<para>开关名称。</para></param>
    /// <returns>The value, or null when absent.<para>值；缺失时为 null。</para></returns>
    public static string? FindValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Gets the extensions carried by the helper arguments after the result file pair.
    /// <para>获取助手参数中结果文件对之后携带的扩展名。</para></summary>
    /// <param name="args">Process arguments.<para>进程参数。</para></param>
    /// <returns>The selected extensions.<para>选定的扩展名集合。</para></returns>
    public static IReadOnlyList<string> ReadExtensions(string[] args)
    {
        var values = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == ResultSwitch)
            {
                i++;
                continue;
            }

            values.Add(args[i]);
        }

        return values;
    }

    /// <summary>Runs registration in this helper process and writes the outcome to the result file.
    /// <para>在本助手进程中执行注册，并把结果写入结果文件。</para></summary>
    /// <param name="args">Process arguments.<para>进程参数。</para></param>
    /// <returns>0 on success, 1 otherwise.<para>成功时为 0，否则为 1。</para></returns>
    public static int Run(string[] args)
    {
        FileAssociationResult result;
        try
        {
            result = FileAssociationService.RegisterDetailed(ReadExtensions(args));
        }
        catch (Exception ex)
        {
            result = FileAssociationResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }

        var resultPath = FindValue(args, ResultSwitch);
        if (!string.IsNullOrWhiteSpace(resultPath))
        {
            try
            {
                File.WriteAllText(resultPath,
                    string.Join('\n', result.Status.ToString(), result.AssociationCount.ToString(), result.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result.Status == FileAssociationStatus.Succeeded ? 0 : 1;
    }

    /// <summary>Reads a helper result file written by <see cref="Run"/>.
    /// <para>读取由 <see cref="Run"/> 写入的助手结果文件。</para></summary>
    /// <param name="resultPath">The result file path.<para>结果文件路径。</para></param>
    /// <returns>The parsed result, or null when the file is missing or malformed.<para>解析出的结果；文件缺失或格式错误时为 null。</para></returns>
    public static FileAssociationResult? ReadResult(string resultPath)
    {
        try
        {
            if (!File.Exists(resultPath))
            {
                return null;
            }

            var lines = File.ReadAllLines(resultPath);
            if (lines.Length < 2 || !Enum.TryParse<FileAssociationStatus>(lines[0], out var status) ||
                !int.TryParse(lines[1], out var count))
            {
                return null;
            }

            return new FileAssociationResult(status, lines.Length > 2 ? lines[2] : string.Empty, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}