using System.Text.RegularExpressions;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Workflow;

/// <summary>Conservatively removes Android verity and forced-encryption fs_mgr flags from fstab text.</summary>
public static partial class FstabVerificationEditor
{
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