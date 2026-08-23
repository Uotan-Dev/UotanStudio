namespace UotanStudio.Firmware.Cpio.Formats.Cpio;

/// <summary>
/// Constants for the cpio family of archive formats.
/// <para>cpio 系列归档格式的常量。</para>
/// </summary>
internal static class CpioConstants
{
    public const string NewAsciiMagic = "070701";
    public const string CrcAsciiMagic = "070702";
    public const string OldAsciiMagic = "070707";
    public const ushort BinaryMagic = 0x71C7; // octal 070707
    public const string TrailerName = "TRAILER!!!";

    public const int NewAsciiHeaderSize = 110;
    public const int OldAsciiHeaderSize = 76;
    public const int OldBinaryHeaderSize = 26;
}