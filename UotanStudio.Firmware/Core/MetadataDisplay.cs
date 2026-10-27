using System.Globalization;

namespace UotanStudio.Firmware.Core;

/// <summary>Invariant, lossless formatting for firmware inspection fields. <para>固件检视字段的固定区域、无损格式化。</para></summary>
internal static class MetadataDisplay
{
    internal static string Number<T>(T value) where T : IFormattable
        => value.ToString(null, CultureInfo.InvariantCulture);

    internal static string Flags(uint value, params (uint Mask, string Name)[] flags)
    {
        var names = flags.Where(flag => (value & flag.Mask) != 0).Select(flag => flag.Name);
        var text = string.Join(", ", names);
        return text.Length == 0 ? $"0x{value:X8}" : $"0x{value:X8} ({text})";
    }

    internal static string Uuid(byte[] bytes) => new Guid(bytes, bigEndian: true).ToString();
}