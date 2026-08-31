using System.Runtime.CompilerServices;

// Tests in UotanStudio.Tests exercise internal seams of the Comm library:
// - subclassing the internal abstract UsbDevice backend type for UsbProviderProjection
//   projection tests (Reflection cannot subclass internal types);
// - the USB monitoring/dedup pure logic (UsbDeviceMonitor, UsbDeviceIdentity) and
//   UsbProviderProjection / UsbDeviceInfoFactory, which are internal but take only
//   public value types.
// <para>UotanStudio.Tests 测试需要访问 Comm 库的内部接缝：
// - 派生内部抽象 UsbDevice 后端类型以测试 UsbProviderProjection（反射无法派生内部类型）；
// - USB 监控/去重纯逻辑（UsbDeviceMonitor、UsbDeviceIdentity）以及 UsbProviderProjection /
//   UsbDeviceInfoFactory，它们均为内部类型但只接收公共值类型。</para>
[assembly: InternalsVisibleTo("UotanStudio.Tests")]
