namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>Sets or deletes a user-namespace xattr; null means absent and empty bytes mean an existing empty value.
/// <para>设置或删除 user 命名空间的扩展属性；<see langword="null"/> 表示属性不存在，空字节数组表示存在但值为空。</para></summary>
/// <param name="Path">In-image path of the target entry; a leading <c>/</c> is trimmed and segments must not be empty, <c>.</c> or <c>..</c>.
/// <para>目标项在镜像内的路径；前导 <c>/</c> 会被去除，且路径段不得为空、<c>.</c> 或 <c>..</c>。</para></param>
/// <param name="Name">Attribute name; must start with <c>user.</c> and be 6 to 255 printable ASCII characters.
/// <para>属性名；必须以 <c>user.</c> 开头，长度为 6 至 255 个可打印 ASCII 字符。</para></param>
/// <param name="Value">New value of at most 65536 bytes; <see langword="null"/> removes the attribute.
/// <para>不超过 65536 字节的新值；<see langword="null"/> 表示删除该属性。</para></param>
/// <param name="CheckOldValue">When <see langword="true"/>, the original value must equal <paramref name="ExpectedValue"/> before the edit applies.
/// <para>为 <see langword="true"/> 时，应用编辑前原值必须与 <paramref name="ExpectedValue"/> 相等。</para></param>
/// <param name="ExpectedValue">Old-value guard of at most 65536 bytes; <see langword="null"/> requires the attribute to be absent.
/// <para>至多 65536 字节的旧值守卫；<see langword="null"/> 要求属性原本不存在。</para></param>
public sealed record FileSystemXattrEdit(string Path, string Name, byte[]? Value,
    bool CheckOldValue = false, byte[]? ExpectedValue = null);