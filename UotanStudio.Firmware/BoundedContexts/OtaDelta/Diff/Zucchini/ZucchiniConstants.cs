namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Zucchini;

/// <summary>Defines Chromium Zucchini wire constants. <para>定义 Chromium Zucchini 线格式常量。</para></summary>
public static class ZucchiniConstants
{
    /// <summary>Gets the little-endian Zucc signature. <para>获取小端 Zucc 签名。</para></summary>
    public const uint Magic = 0x6363755A;

    /// <summary>Gets the supported major version. <para>获取支持的主版本。</para></summary>
    public const uint Version = 2;

    /// <summary>Gets the header size excluding the element count. <para>获取不含元素计数的头部大小。</para></summary>
    public const int HeaderByteSize = 24;

    /// <summary>Gets the packed element header size. <para>获取紧凑元素头大小。</para></summary>
    public const int ElementHeaderByteSize = 22;
}