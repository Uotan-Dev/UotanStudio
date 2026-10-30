using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend;

/// <summary>
/// Walks a raw USB configuration descriptor and reports the interfaces it declares, each with its endpoints.
/// <para>遍历原始 USB 配置描述符，报告其中声明的接口及其端点。</para>
/// </summary>
internal static class UsbConfigurationDescriptorParser
{
    private const byte InterfaceDescriptorType = 4;
    private const byte EndpointDescriptorType = 5;

    /// <summary>
    /// Parses a configuration descriptor held in native memory.
    /// <para>解析位于非托管内存中的配置描述符。</para>
    /// </summary>
    /// <param name="buffer">Pointer to the raw configuration descriptor; <see cref="IntPtr.Zero"/> yields no interface.
    /// <para>原始配置描述符指针；为 <see cref="IntPtr.Zero"/> 时不返回任何接口。</para></param>
    /// <param name="length">Total bytes readable at <paramref name="buffer"/>; negative values are rejected.
    /// <para><paramref name="buffer"/> 处可读的总字节数；负值会被拒绝。</para></param>
    /// <returns>The parsed interfaces with their endpoints, in descriptor order.
    /// <para>解析出的接口及其端点，顺序与描述符一致。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.
    /// <para><paramref name="length"/> 为负值。</para></exception>
    internal static List<UsbInterfaceInfo> Parse(IntPtr buffer, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (buffer == IntPtr.Zero || length < 9)
        {
            return [];
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Marshal.Copy(buffer, rented, 0, length);
            return Parse(rented.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Parses a configuration descriptor held in a managed span.
    /// <para>解析位于托管切片中的配置描述符。</para>
    /// </summary>
    /// <param name="descriptor">The raw configuration descriptor bytes, starting at <c>bLength</c>.
    /// <para>原始配置描述符字节，从 <c>bLength</c> 开始。</para></param>
    /// <returns>The parsed interfaces with their endpoints, in descriptor order.
    /// <para>解析出的接口及其端点，顺序与描述符一致。</para></returns>
    internal static List<UsbInterfaceInfo> Parse(ReadOnlySpan<byte> descriptor)
    {
        var result = new List<UsbInterfaceInfo>();
        if (descriptor.Length < 9)
        {
            return result;
        }

        var length = descriptor.Length;
        var pos = 0;
        UsbInterfaceInfo? currentInterface = null;
        List<UsbEndpointInfo>? currentEndpoints = null;
        while (pos + 2 <= length)
        {
            int bLength = descriptor[pos];
            int bDescriptorType = descriptor[pos + 1];
            if (bLength < 2 || pos + bLength > length)
            {
                break;
            }

            switch (bDescriptorType)
            {
                case InterfaceDescriptorType when bLength >= 8:
                    if (currentInterface != null)
                    {
                        result.Add(currentInterface);
                    }

                    currentEndpoints = [];
                    currentInterface = new UsbInterfaceInfo
                    {
                        InterfaceNumber = descriptor[pos + 2],
                        Class = descriptor[pos + 5],
                        SubClass = descriptor[pos + 6],
                        Protocol = descriptor[pos + 7],
                        Endpoints = currentEndpoints
                    };
                    break;

                case EndpointDescriptorType when bLength >= 7:
                    currentEndpoints?.Add(new UsbEndpointInfo
                    {
                        EndpointAddress = descriptor[pos + 2],
                        Attributes = descriptor[pos + 3],
                        MaxPacketSize = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[(pos + 4)..]),
                        Interval = descriptor[pos + 6]
                    });
                    break;

                default:
                    break;
            }

            pos += bLength;
        }

        if (currentInterface != null)
        {
            result.Add(currentInterface);
        }

        return result;
    }
}
