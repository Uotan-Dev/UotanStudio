namespace UotanStudio.Firmware.Zucchini;

/// <summary>
/// Identifies the executable format of a Zucchini patch element.
/// <para>标识 Zucchini 补丁元素的可执行文件格式。</para>
/// </summary>
public enum ZucchiniExecutableType : uint
{
    /// <summary>
    /// RAW: the element's bytes are treated as opaque data and applied without any disassembly or
    /// reference handling. This is the only executable type supported by this applier; other values
    /// cause <see cref="ZucchiniUnsupportedExecutableTypeException"/>.
    /// <para>RAW：元素的字节按不透明数据处理，不做反汇编或引用处理。这是本应用器唯一支持的
    /// 可执行类型；其他取值将导致 <see cref="ZucchiniUnsupportedExecutableTypeException"/>。</para>
    /// </summary>
    Raw = 0
}