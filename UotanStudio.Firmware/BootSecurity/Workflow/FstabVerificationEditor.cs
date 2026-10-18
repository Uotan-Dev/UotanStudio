using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BootSecurity.Workflow;

/// <summary>Conservatively removes Android verity and forced-encryption fs_mgr flags from fstab text.
/// <para>以保守方式从 fstab 文本中移除 Android verity 与强制加密的 fs_mgr 标记。</para></summary>
public static partial class FstabVerificationEditor
{
    /// <summary>Strips verity and forced-encryption mount flags from fstab text using case-insensitive matching.
    /// <para>以不区分大小写的匹配从 fstab 文本中移除 verity 与强制加密挂载标记。</para></summary>
    /// <param name="text">Full fstab text to patch.<para>要打补丁的完整 fstab 文本。</para></param>
    /// <param name="disableVerity">When <see langword="true"/>, a first pass removes whole <c>,avb_keys=…</c> options, then a second pass removes <c>avb</c> and <c>verify</c> flags with their values. The key option is removed first so the generic <c>avb</c> rule cannot consume only its <c>avb</c> prefix and strand <c>_keys=…</c>.
    /// <para>为 <see langword="true"/> 时，先整段移除 <c>,avb_keys=…</c> 选项，再移除 <c>avb</c> 与 <c>verify</c> 标记（含其取值）。键选项先移除，通用 <c>avb</c> 规则才不会只吃掉它的 <c>avb</c> 前缀 而把 <c>_keys=…</c> 残留下来。</para></param>
    /// <param name="disableForceEncryption">When <see langword="true"/>, removes <c>forceencrypt</c>, <c>fileencryption</c> and <c>forcefdeorfbe</c> flags with their values.
    /// <para>为 <see langword="true"/> 时移除 <c>forceencrypt</c>、<c>fileencryption</c> 与 <c>forcefdeorfbe</c> 标记及其取值。</para></param>
    /// <returns>The patched text. Only whole <c>,</c>- or whitespace-delimited tokens whose name is exactly one of the target flags are removed, so unrelated flags such as <c>avbsomething=1</c>, <c>fileencryptionmode=x</c> or <c>verifyatboot</c> survive untouched; the delimiter that preceded a removed flag is kept, and a record separator is never swallowed, so the line structure of the fstab is preserved.
    /// <para>打补丁后的文本。只有名称恰好等于目标标记、且以 <c>,</c> 或空白分隔的完整 token 会被移除， 因此 <c>avbsomething=1</c>、<c>fileencryptionmode=x</c>、<c>verifyatboot</c> 这类无关标记原样保留； 被移除标记前的分隔符得以保留，记录分隔符绝不会被吞掉，因此 fstab 的行结构不变。</para></returns>
    /// <exception cref="ArgumentNullException">The text is <see langword="null"/>.
    /// <para>文本为 <see langword="null"/>。</para></exception>
    public static string Patch(string text, bool disableVerity, bool disableForceEncryption)
    {
        ArgumentNullException.ThrowIfNull(text);
        var updated = text;
        if (disableVerity)
        {
            updated = AvbKeysRegex().Replace(updated, string.Empty);
            updated = VerityFlagRegex().Replace(updated, "$1");
        }
        if (disableForceEncryption)
        {
            updated = EncryptionFlagRegex().Replace(updated, "$1");
        }
        return updated;
    }

    [GeneratedRegex(@"(?i)([,\s]|^)(avb|verify)(=[^,\s]*)?(?=[,\s]|$)")]
    private static partial Regex VerityFlagRegex();

    [GeneratedRegex(@"(?i),avb_keys=[^,\s]*")]
    private static partial Regex AvbKeysRegex();

    [GeneratedRegex(@"(?i)([,\s]|^)(forceencrypt|fileencryption|forcefdeorfbe)(=[^,\s]*)?(?=[,\s]|$)")]
    private static partial Regex EncryptionFlagRegex();
}