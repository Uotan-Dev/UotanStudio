using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Contracts;

/// <summary>
/// Immutable observed USB identity used to validate a selected by-key open.
/// <para>不可变的已观测 USB 身份，用于校验按所选键发起的开启。</para>
/// </summary>
/// <param name="Backend">
/// USB API kind that enumerated the device; the opener must use the same backend to reach it.
/// <para>枚举出该设备的 USB API 种类；开启器必须使用同一后端才能访问它。</para>
/// </param>
/// <param name="Path">
/// Operating-system device path recorded at discovery; a by-key open filters on this exact value.
/// <para>发现时记录的操作系统设备路径；按键开启会严格按此值筛选。</para>
/// </param>
/// <param name="VendorId">
/// 16-bit USB vendor identifier as reported by enumeration.
/// <para>枚举上报的 16 位 USB 厂商标识。</para>
/// </param>
/// <param name="ProductId">
/// 16-bit USB product identifier as reported by enumeration.
/// <para>枚举上报的 16 位 USB 产品标识。</para>
/// </param>
/// <param name="InterfaceClass">
/// Class code of the matched interface, or <see langword="null"/> when it was not observed.
/// <para>匹配接口的类代码；未观测到时为 <see langword="null"/>。</para>
/// </param>
/// <param name="InterfaceSubClass">
/// Subclass code of the matched interface, or <see langword="null"/> when it was not observed.
/// <para>匹配接口的子类代码；未观测到时为 <see langword="null"/>。</para>
/// </param>
/// <param name="InterfaceProtocol">
/// Protocol code of the matched interface, or <see langword="null"/> when it was not observed.
/// <para>匹配接口的协议代码；未观测到时为 <see langword="null"/>。</para>
/// </param>
public sealed record UsbEndpointDetails(
    UsbApiKind Backend, string Path, ushort VendorId, ushort ProductId,
    byte? InterfaceClass, byte? InterfaceSubClass, byte? InterfaceProtocol);