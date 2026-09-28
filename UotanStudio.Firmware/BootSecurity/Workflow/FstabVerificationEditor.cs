using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BootSecurity.Workflow;

/// <summary>Conservatively removes Android verity and forced-encryption fs_mgr flags from fstab text.
/// <para>以保守方式从 fstab 文本中移除 Android verity 与强制加密的 fs_mgr 标记。</para></summary>
public static partial class FstabVerificationEditor
{
    /// <summary>
    /// Strips verity and forced-encryption mount flags from fstab text using case-insensitive matching.
    /// <para>以不区分大小写的匹配从 fstab 文本中移除 verity 与强制加密挂载标记。</para>
    /// </summary>
    /// <param name="text">Full fstab text to patch. <para>要打补丁的完整 fstab 文本。</para></param>
    /// <param name="disableVerity">When <see langword="true"/>, a first pass removes <c>avb</c> and
    /// <c>verify</c> flags (with their values) while keeping the delimiter, then a second pass strips
    /// <c>,avb_keys=…</c> options.
    /// <para>为 <see langword="true"/> 时，先移除 <c>avb</c> 与 <c>verify</c> 标记（含其取值）并保留分隔符，
    /// 再由第二遍剥离 <c>,avb_keys=…</c> 选项。</para></param>
    /// <param name="disableForceEncryption">When <see langword="true"/>, removes <c>forceencrypt</c>,
    /// <c>fileencryption</c> and <c>forcefdeorfbe</c> flags with their values.
    /// <para>为 <see langword="true"/> 时移除 <c>forceencrypt</c>、<c>fileencryption</c> 与
    /// <c>forcefdeorfbe</c> 标记及其取值。</para></param>
    /// <returns>
    /// The patched text; flag removal keeps the surrounding field delimiter, and unflagged content is
    /// returned unchanged.
    /// <para>打补丁后的文本；移除标记时保留周围的字段分隔符，未含标记的内容原样返回。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">The text is <see langword="null"/>.
    /// <para>文本为 <see langword="null"/>。</para></exception>
    public static string Patch(string text, bool disableVerity, bool disableForceEncryption)
    {
        ArgumentNullException.ThrowIfNull(text);
        var updated = text;
        if (disableVerity)
        {
            updated = VerityFlagRegex().Replace(updated, "$1");
            updated = AvbKeysRegex().Replace(updated, string.Empty);
        }
        if (disableForceEncryption)
        {
            updated = EncryptionFlagRegex().Replace(updated, "$1");
        }
        return updated;
    }

    [GeneratedRegex(@"(?i)([, ]|^)(avb|verify)(=[^, ]+)?")]
    private static partial Regex VerityFlagRegex();

    [GeneratedRegex(@"(?i),avb_keys=[^, ]+")]
    private static partial Regex AvbKeysRegex();

    [GeneratedRegex(@"(?i)([, ]|^)(forceencrypt|fileencryption|forcefdeorfbe)(=[^, ]+)?")]
    private static partial Regex EncryptionFlagRegex();
}