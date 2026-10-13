using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Protocols.Adb;

namespace UotanStudio.Comm.Devices;

/// <summary>
/// Adapters that expose internal protocol streams as base BCL types, hiding implementation types.
/// <para>把内部协议流以 BCL 基类型暴露的适配器，隐藏具体的实现类型。</para>
/// </summary>
public static class AdbStreamExtensions
{
    extension(IAdbStream stream)
    {
        /// <summary>Wraps a raw ADB packet stream as a byte-oriented <see cref="Stream"/>.
        /// <para>将 ADB 原始数据包流包装为面向字节的 <see cref="Stream"/>。</para></summary>
        /// <returns>A <see cref="Stream"/> facade that disposes the underlying ADB stream together
        /// with the wrapper.
        /// <para>与包装器一并释放底层 ADB 流的 <see cref="Stream"/> 门面。</para></returns>
        public Stream AsServiceStream() => new AdbServiceStream(stream);
    }
}