using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Lockdown;

/// <summary>
/// Stores host pair records per device serial under the user's local application data and can import
/// the pair records the Apple software stack keeps under <c>ProgramData</c>.
/// <para>按设备序列号在用户本地应用数据下保存主机配对记录，并可导入 Apple 软件栈保存在
/// <c>ProgramData</c> 下的配对记录。</para>
/// </summary>
public sealed class LockdownPairingRecordStore
{
    private readonly string _directory;

    /// <summary>
    /// Creates a store rooted at the supplied directory, defaulting to
    /// <c>%LOCALAPPDATA%/UotanStudio/apple-lockdown</c>.
    /// <para>以给定目录为根创建存储，默认为 <c>%LOCALAPPDATA%/UotanStudio/apple-lockdown</c>。</para>
    /// </summary>
    /// <param name="directory">The storage directory, or <see langword="null"/> for the default. <para>存储目录；<see langword="null"/> 时使用默认。</para></param>
    public LockdownPairingRecordStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UotanStudio", "apple-lockdown");
        Directory.CreateDirectory(_directory);
    }

    /// <summary>
    /// Gets the persistent system build identifier, creating it on first use.
    /// <para>获取持久的系统构建标识，首次使用时创建。</para>
    /// </summary>
    /// <returns>The BUID string. <para>BUID 字符串。</para></returns>
    public string GetSystemBuid()
    {
        var path = Path.Combine(_directory, "SystemBUID");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length is 32 && existing.All(char.IsAsciiHexDigit))
            {
                return existing;
            }
        }

        var buid = string.Create(32, (object?)null, static (span, _) =>
        {
            RandomNumberGenerator.GetItems<char>("0123456789ABCDEF", span);
        });
        WriteAtomically(path, buid);
        return buid;
    }

    /// <summary>
    /// Loads the stored pair record for a serial.
    /// <para>读取某序列号已保存的配对记录。</para>
    /// </summary>
    /// <param name="serial">The device serial (UDID). <para>设备序列号（UDID）。</para></param>
    /// <returns>The record, or <see langword="null"/> when absent or unreadable. <para>记录；缺失或不可读时为 <see langword="null"/>。</para></returns>
    public LockdownPairingRecord? Load(string serial)
    {
        var record = TryLoadPairRecordPlist(Path.Combine(_directory, SafeFileName(serial)));
        if (record is not null)
        {
            return record;
        }

        return TryImportAppleRecord(serial);
    }

    /// <summary>
    /// Saves the pair record for a serial with an atomic replace.
    /// <para>以原子替换保存某序列号的配对记录。</para>
    /// </summary>
    /// <param name="serial">The device serial (UDID). <para>设备序列号（UDID）。</para></param>
    /// <param name="record">The record to persist. <para>待保存的记录。</para></param>
    public void Save(string serial, LockdownPairingRecord record) =>
        WriteAtomically(Path.Combine(_directory, SafeFileName(serial)),
            Encoding.UTF8.GetString(ApplePlist.ToXml(record.ToPlist())));

    /// <summary>
    /// Deletes the stored pair record for a serial.
    /// <para>删除某序列号已保存的配对记录。</para>
    /// </summary>
    /// <param name="serial">The device serial (UDID). <para>设备序列号（UDID）。</para></param>
    public void Delete(string serial)
    {
        var path = Path.Combine(_directory, SafeFileName(serial));
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private LockdownPairingRecord? TryLoadPairRecordPlist(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (ApplePlist.Parse(File.ReadAllBytes(path)) is not Dictionary<string, object> plist)
            {
                return null;
            }

            return LockdownPairingRecord.FromPlist(plist);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private LockdownPairingRecord? TryImportAppleRecord(string serial)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var candidate in new[]
                 {
                     Path.Combine(programData, "Apple", "Lockdown", $"{serial}.plist"),
                     Path.Combine(programData, "Apple", "Lockdown", SafeFileName(serial)),
                 })
        {
            var imported = TryLoadPairRecordPlist(candidate);
            if (imported is not null)
            {
                return imported;
            }
        }

        return null;
    }

    private static void WriteAtomically(string path, string content)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string SafeFileName(string serial)
    {
        var builder = new StringBuilder(serial.Length);
        foreach (var character in serial)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }
}