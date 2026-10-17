using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;

namespace UotanStudio.Firmware.BootSecurity.Workflow;

/// <summary>
/// Reads only the boot header for plans that never touch the kernel or ramdisk payload.
/// <para>为不触碰 kernel 或 ramdisk 负载的方案只读取 boot 头部。</para>
/// </summary>
internal static class BootHeaderSource
{
    /// <summary>
    /// Reads the header block and component compression signatures without materializing the image.
    /// <para>读取头部块与组件压缩签名，不物化整个镜像。</para>
    /// </summary>
    public static BootImageHeader Read(string bootPath)
    {
        try
        {
            return BootImageParser.ReadHeaderFile(bootPath);
        }
        catch (EndOfStreamException ex)
        {
            throw new ImageParseException(ex.Message, ex);
        }
        catch (InvalidDataException ex)
        {
            throw new ImageParseException(ex.Message, ex);
        }
    }
}