namespace UotanStudio.Comm.Protocols.Apple;

/// <summary>
/// A binary property list UID value (keyed archives); round-trips as an unsigned integer tag.
/// <para>二进制属性列表的 UID 值（keyed archive）；按无符号整数标签往返。</para>
/// </summary>
/// <param name="Value">The unsigned identifier. <para>无符号标识值。</para></param>
public sealed record ApplePlistUid(ulong Value);