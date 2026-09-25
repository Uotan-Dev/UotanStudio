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
    /// <param name="stream">The ADB stream to adapt; the returned wrapper reads and writes through it.
    /// <para>待适配的 ADB 流；返回的包装器经由该流读写。</para></param>
    /// <returns>A <see cref="Stream"/> facade over <paramref name="stream"/>. Disposing the wrapper disposes the underlying ADB stream, so callers must stop using <paramref name="stream"/> once the wrapper is disposed.
    /// <para>基于 <paramref name="stream"/> 的 <see cref="Stream"/> 门面。释放该包装器会一并释放底层 ADB 流，因此包装器被释放后调用方不得再使用 <paramref name="stream"/>。</para></returns>
    public static Stream AsServiceStream(this IAdbStream stream) => new AdbServiceStream(stream);
}