using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Services;

/// <summary>
/// Registers/unregisters Windows file associations for the UotanStudio formats (<c>.uotan</c>,
/// <c>.uotanfw</c>, <c>.uotannode</c>) in <c>HKCU\Software\Classes</c> (no admin required), wiring the
/// default open command to the current executable and a per-format <c>DefaultIcon</c>. Uses P/Invoke to
/// advapi32 (AOT-safe, no package dependency). No-op on non-Windows.
/// <para>在 <c>HKCU\Software\Classes</c>（无需管理员权限）注册/注销 UotanStudio 格式（<c>.uotan</c>、
/// <c>.uotanfw</c>、<c>.uotannode</c>）的 Windows 文件关联，将默认打开命令指向当前可执行文件并为每种格式
/// 设置 <c>DefaultIcon</c>。通过 P/Invoke 调用 advapi32（AOT 安全，无包依赖）。非 Windows 平台无操作。</para>
/// </summary>
public static partial class FileAssociationService
{
    private const string ClassesRoot = @"Software\Classes";
    private const int RegSz = 1;
    private const int RegOptionNonVolatile = 0;
    private const int KeyWrite = 0x00020006;
    private const int KeyRead = 0x00020019;

    // HKEY_CURRENT_USER 伪句柄（64 位下为 0x0000000080000001）。
    // HKEY_CURRENT_USER pseudo-handle (0x0000000080000001 on 64-bit).
    private static readonly nint Hkcu = new(0x80000001L);

    private static readonly (string Extension, string ProgId, string IconFile, string Description)[] Formats =
    [
        (".uotan", "UotanStudio.uotan", "uotan.ico", "UotanStudio Solution"),
        (".uotanfw", "UotanStudio.uotanfw", "uotanfw.ico", "UotanStudio Firmware Project"),
        (".uotannode", "UotanStudio.uotannode", "uotannode.ico", "UotanStudio Node Project")
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
    /// Registers the file associations idempotently; returns whether the registration succeeded.
    /// <para>幂等注册文件关联；返回注册是否成功。</para>
    /// </summary>
    public static bool Register()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            var iconDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
            foreach (var (extension, progId, iconFile, description) in Formats)
            {
                SetStringValue(Path.Combine(ClassesRoot, progId), null, description);
                SetStringValue(Path.Combine(ClassesRoot, progId, "DefaultIcon"), null, $"\"{Path.Combine(iconDirectory, iconFile)}\",0");
                SetStringValue(Path.Combine(ClassesRoot, progId, "shell", "open", "command"), null, $"\"{exePath}\" \"%1\"");
                SetStringValue(Path.Combine(ClassesRoot, extension), null, progId);
            }

            LogService.Instance.Write(LogLevel.Info, "FileAssociation", $"Registered {Formats.Length} file association(s)");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "FileAssociation", $"Register failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes the file associations; returns whether the unregistration succeeded.
    /// <para>移除文件关联；返回注销是否成功。</para>
    /// </summary>
    public static bool Unregister()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            foreach (var (extension, progId, _, _) in Formats)
            {
                RegDeleteTreeW(Hkcu, Path.Combine(ClassesRoot, progId));

                // 仅当扩展名默认值指向本应用 ProgId 时才删除扩展名键，避免误删其他应用关联。
                // Only delete the extension key when its default value points to our ProgId, to avoid
                // clobbering another application's association.
                if (GetStringValue(Path.Combine(ClassesRoot, extension), null) == progId)
                {
                    RegDeleteTreeW(Hkcu, Path.Combine(ClassesRoot, extension));
                }
            }

            LogService.Instance.Write(LogLevel.Info, "FileAssociation", $"Unregistered {Formats.Length} file association(s)");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "FileAssociation", $"Unregister failed: {ex.Message}");
            return false;
        }
    }

    private static void SetStringValue(string subKey, string? valueName, string value)
    {
        if (RegCreateKeyExW(Hkcu, subKey, 0, null, RegOptionNonVolatile, KeyWrite, nint.Zero, out var key, out _) != 0)
        {
            throw new InvalidOperationException($"Failed to open/create registry key '{subKey}'.");
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
}