using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;

namespace UotanStudio.Comm.Devices;

/// <summary>
/// Adapters that expose internal protocol streams as base BCL types so callers never name
/// an implementation type.
/// <para>把内部协议流以 BCL 基类型暴露给调用方的适配器，使调用方无需指名实现类型。</para>
/// </summary>
public static class AdbStreamExtensions
{
    /// <summary>Wraps a raw ADB packet stream as a byte-oriented <see cref="Stream"/>.
    /// <para>将 ADB 原始数据包流包装为面向字节的 <see cref="Stream"/>。</para></summary>
    public static Stream AsServiceStream(this IAdbStream stream) => new AdbServiceStream(stream);
}