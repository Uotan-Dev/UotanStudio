namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>
/// Validation rules used by APatch for a user supplied SuperKey.
/// <para>APatch 对用户提供的 SuperKey 所使用的校验规则。</para>
/// </summary>
internal static class APatchSuperKey
{
    /// <summary>
    /// APatch accepts 8-63 character keys containing at least one letter and digit.
    /// <para>APatch 接受长度为 8-63 个字符、且至少包含一个字母和一个数字的密钥。</para>
    /// </summary>
    /// <param name="value">Candidate key; <c>null</c> is always rejected. <para>候选密钥；<c>null</c> 一律不通过。</para></param>
    /// <returns><see langword="true"/> when the key satisfies the length and character-class rules.
    /// <para>当密钥满足长度与字符类别规则时返回 <see langword="true"/>。</para></returns>
    public static bool IsValid(string? value)
        => value is { Length: >= 8 and <= 63 }
           && value.Any(char.IsLetter)
           && value.Any(char.IsDigit);
}