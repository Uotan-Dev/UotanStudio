using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using static UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS.HarmonyOSUsbDDK;

namespace UotanStudio.Comm.Transports.Usb.Backend.HarmonyOS;

internal static class HarmonyOSUsbFinder
{
    /// <summary>
    /// Gets or sets the highest device ID probed during discovery (default 256). Callers that know
    /// their device ID raise it to scan further, or lower it to shorten the scan.
    /// <para>获取或设置发现期间探测的最大设备 ID（默认 256）。已知设备 ID 的调用方可调大以继续向后扫描，或调小以缩短扫描。</para>
    /// </summary>
    public static int MaxDeviceId { get; set; } = 256;

    public static List<UsbDevice> FindDevice(UsbDeviceFilter? filter = null)
    {
        var devices = new List<UsbDevice>();

        var ret = OH_Usb_Init();
        if (ret != USB_DDK_NO_ERROR)
        {
            UsbTrace.Log($"HarmonyOSUsbFinder: OH_Usb_Init failed: {GetErrorMessage(ret)}");
            return devices;
        }

        try
        {
            var maxProbe = (ulong)Math.Max(0, MaxDeviceId);
            for (ulong deviceId = 1; deviceId <= maxProbe; deviceId++)
            {
                ProbeDevice(deviceId, filter, devices);
            }
        }
        finally
        {
            OH_Usb_Release();
        }

        return devices;
    }

    private static void ProbeDevice(ulong deviceId, UsbDeviceFilter? filter, List<UsbDevice> devices)
    {
        var devDesc = new UsbDeviceDescriptor();
        var ret = OH_Usb_GetDeviceDescriptor(deviceId, ref devDesc);
        if (ret != USB_DDK_NO_ERROR)
        {
            return;
        }

        var vendorId = devDesc.IdVendor;
        var productId = devDesc.IdProduct;

        if (filter?.VendorId is ushort filterVid && vendorId != filterVid)
        {
            return;
        }

        if (filter?.ProductId is ushort filterPid && productId != filterPid)
        {
            return;
        }

        var configPtr = IntPtr.Zero;
        ret = OH_Usb_GetConfigDescriptor(deviceId, 0, ref configPtr);
        if (ret != USB_DDK_NO_ERROR || configPtr == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var configDescriptor = Marshal.PtrToStructure<UsbDdkConfigDescriptor>(configPtr);
            var numInterfaces = configDescriptor.NumIface;
            var interfaces = new List<UsbInterfaceInfo>();

            for (byte ifcIndex = 0; ifcIndex < numInterfaces; ifcIndex++)
            {
                var ifaceArrayPtr = configDescriptor.Iface;
                if (ifaceArrayPtr == IntPtr.Zero)
                {
                    continue;
                }

                var ifaceSize = Marshal.SizeOf<UsbDdkInterface>();
                var ifacePtr = new IntPtr(ifaceArrayPtr.ToInt64() + (ifcIndex * ifaceSize));

                var iface = Marshal.PtrToStructure<UsbDdkInterface>(ifacePtr);

                if (iface.Altsetting == IntPtr.Zero)
                {
                    continue;
                }

                var ifaceDescStruct = Marshal.PtrToStructure<UsbDdkInterfaceDescriptor>(iface.Altsetting);
                var ifaceDesc = ifaceDescStruct.InterfaceDescriptor;

                var ifcClass = ifaceDesc.BInterfaceClass;
                var ifcSubClass = ifaceDesc.BInterfaceSubClass;
                var ifcProtocol = ifaceDesc.BInterfaceProtocol;

                var numEndpoints = ifaceDesc.BNumEndpoints;
                var endpoints = new List<UsbEndpointInfo>();
                byte epIn = 0, epOut = 0;

                var endpointArrayPtr = ifaceDescStruct.Endpoint;
                if (endpointArrayPtr != IntPtr.Zero)
                {
                    var epSize = Marshal.SizeOf<UsbEndpointDescriptor>();
                    for (var epIdx = 0; epIdx < numEndpoints; epIdx++)
                    {
                        var epPtr = new IntPtr(endpointArrayPtr.ToInt64() + (epIdx * epSize));
                        var epDesc = Marshal.PtrToStructure<UsbEndpointDescriptor>(epPtr);

                        var epAddr = epDesc.BEndpointAddress;
                        var epAttr = epDesc.BmAttributes;
                        endpoints.Add(new UsbEndpointInfo
                        {
                            EndpointAddress = epAddr,
                            Attributes = epAttr,
                            MaxPacketSize = epDesc.WMaxPacketSize,
                            Interval = epDesc.BInterval
                        });

                        if ((epAttr & 0x03) == 0x02)
                        {
                            if ((epAddr & 0x80) != 0)
                            {
                                epIn = epAddr;
                            }
                            else
                            {
                                epOut = epAddr;
                            }
                        }
                    }
                }

                interfaces.Add(new UsbInterfaceInfo
                {
                    InterfaceNumber = ifaceDesc.BInterfaceNumber,
                    Class = ifcClass,
                    SubClass = ifcSubClass,
                    Protocol = ifcProtocol,
                    Endpoints = endpoints
                });

                if (filter?.InterfaceClass is byte c && ifcClass != c)
                {
                    continue;
                }

                if (filter?.InterfaceSubClass is byte s && ifcSubClass != s)
                {
                    continue;
                }

                if (filter?.InterfaceProtocol is byte p && ifcProtocol != p)
                {
                    continue;
                }

                if (filter?.InterfaceNumber is byte n && ifaceDesc.BInterfaceNumber != n)
                {
                    continue;
                }

                if (epIn != 0 && epOut != 0)
                {
                    var dev = new HarmonyOSUsbDevice();
                    dev.Initialize(
                        deviceId,
                        ifcIndex,
                        epIn,
                        epOut,
                        vendorId,
                        productId,
                        ifcClass,
                        ifcSubClass,
                        ifcProtocol,
                        null
                    );
                    dev.Interfaces = interfaces;
                    dev.BcdUsb = devDesc.BcdUSB;
                    dev.Speed = UsbSpeedInference.FromBcdUsb(devDesc.BcdUSB);

                    devices.Add(dev);

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            UsbTrace.Log($"HarmonyOSUsbFinder: ProbeDevice failed for device {deviceId}: {ex.Message}");
        }
        finally
        {
            OH_Usb_FreeConfigDescriptor(configPtr);
        }
    }
}