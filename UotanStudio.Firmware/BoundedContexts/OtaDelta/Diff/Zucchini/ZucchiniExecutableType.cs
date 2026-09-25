namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.Zucchini;

/// <summary>
/// Executable-type identifier a Zucchini patch element carries; the stored value is the four ASCII bytes of the
/// element name read as a little-endian unsigned integer.
/// <para>Zucchini 补丁元素携带的可执行类型标识；存储值即元素名四个 ASCII 字节按小端无符号整数读取的结果。</para>
/// </summary>
public enum ZucchiniExecutableType : uint
{
    /// <summary>
    /// NoOp element: the patch stores equivalence runs and raw deltas only, with no executable-specific model.
    /// <para>NoOp 元素：补丁仅保存等价串与原始增量，不含可执行文件专用模型。</para>
    /// </summary>
    Raw = 0x704F6F4E
}