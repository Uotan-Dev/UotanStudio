using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using Xunit;
using UsbdevfsUrb = UotanStudio.Comm.Transports.Usb.Backend.Linux.LinuxUsbAPI.Usbdevfs_urb;

namespace UotanStudio.Tests.Devices;

public sealed class LinuxUsbfsUrbAbiTests
{
    [Fact]
    public void UrbTypeConstants_MatchUsbdeviceFsHeader_UseUsbfsNumbering()
    {
        Assert.Equal((byte)0, LinuxUsbAPI.USBDEVFS_URB_TYPE_ISO);
        Assert.Equal((byte)1, LinuxUsbAPI.USBDEVFS_URB_TYPE_INTERRUPT);
        Assert.Equal((byte)2, LinuxUsbAPI.USBDEVFS_URB_TYPE_CONTROL);
        Assert.Equal((byte)3, LinuxUsbAPI.USBDEVFS_URB_TYPE_BULK);
        Assert.NotEqual(LinuxUsbAPI.USBDEVFS_URB_TYPE_CONTROL, LinuxUsbAPI.USBDEVFS_URB_TYPE_BULK);
    }

    [Fact]
    public void UrbFlagConstants_MatchUsbdeviceFsHeader_DistinguishShortNotOkFromIsoAsap()
    {
        Assert.Equal(0x0001u, LinuxUsbAPI.USBDEVFS_URB_SHORT_NOT_OK);
        Assert.Equal(0x0002u, LinuxUsbAPI.USBDEVFS_URB_ISO_ASAP);
    }

    [Fact]
    public void SetInterfaceAndConfigurationRequestCodes_MatchUsbdeviceFsHeader_UseIorNumbersFourAndFive()
    {
        const uint readDirection = 2u << 30;
        const uint usbdevfsType = (uint)'U' << 8;

        Assert.Equal(readDirection | (8u << 16) | usbdevfsType | 4u, LinuxUsbAPI.USBDEVFS_SETINTERFACE);
        Assert.Equal(readDirection | (4u << 16) | usbdevfsType | 5u, LinuxUsbAPI.USBDEVFS_SETCONFIGURATION);
    }

    [Fact]
    public void SubmitUrbRequestCode_MatchesUrbStructSize_ForCurrentPointerWidth()
    {
        var code = IntPtr.Size == 8
            ? LinuxUsbAPI.USBDEVFS_SUBMITURB_X86_64
            : LinuxUsbAPI.USBDEVFS_SUBMITURB_X86;

        Assert.Equal((uint)Marshal.SizeOf<UsbdevfsUrb>(), (code >> 16) & 0x3FFFu);
        Assert.Equal(2u, (code >> 30) & 0x3u);
        Assert.Equal((uint)'U', (code >> 8) & 0xFFu);
        Assert.Equal(10u, code & 0xFFu);
    }

    [Fact]
    public void UsbdevfsUrb_FieldOffsets_MatchKernelLayout_ForCurrentPointerWidth()
    {
        var bufferOffset = IntPtr.Size == 8 ? 16 : 12;
        var bufferLengthOffset = bufferOffset + IntPtr.Size;
        var usercontextOffset = IntPtr.Size == 8 ? 48 : 40;

        Assert.Equal(0, (int)Marshal.OffsetOf<UsbdevfsUrb>(nameof(UsbdevfsUrb.Type)));
        Assert.Equal(1, (int)Marshal.OffsetOf<UsbdevfsUrb>(nameof(UsbdevfsUrb.Endpoint)));
        Assert.Equal(4, (int)Marshal.OffsetOf<UsbdevfsUrb>(nameof(UsbdevfsUrb.Status)));
        Assert.Equal(8, (int)Marshal.OffsetOf<UsbdevfsUrb>(nameof(UsbdevfsUrb.Flags)));
        Assert.Equal(bufferOffset, (int)Marshal.OffsetOf<UsbdevfsUrb>(nameof(UsbdevfsUrb.Buffer)));
        Assert.Equal(bufferLengthOffset, (int)Marshal.OffsetOf<UsbdevfsUrb>(nameof(UsbdevfsUrb.Buffer_length)));
        Assert.Equal(bufferLengthOffset + 4, (int)Marshal.OffsetOf<UsbdevfsUrb>(nameof(UsbdevfsUrb.Actual_length)));
        Assert.Equal(usercontextOffset, (int)Marshal.OffsetOf<UsbdevfsUrb>(nameof(UsbdevfsUrb.Usercontext)));
    }

    [Fact]
    public void DisconnectRequestCodes_MatchUsbdeviceFsHeader_EncodeTheInterfaceScopedForm()
    {
        const uint writeDirection = 1u << 30;
        const uint readDirection = 2u << 30;
        const uint usbdevfsType = (uint)'U' << 8;

        var ioctlStructBytes = (uint)Marshal.SizeOf<LinuxUsbAPI.Usbdevfs_ioctl>();
        var disconnectClaimBytes = (uint)Marshal.SizeOf<LinuxUsbAPI.Usbdevfs_disconnect_claim>();

        Assert.Equal((uint)'U' << 8 | 22u, LinuxUsbAPI.USBDEVFS_DISCONNECT);
        Assert.Equal(LinuxUsbAPI.USBDEVFS_IOCTL,
            writeDirection | readDirection | (ioctlStructBytes << 16) | usbdevfsType | 18u);
        Assert.Equal(LinuxUsbAPI.USBDEVFS_DISCONNECT_CLAIM,
            readDirection | (disconnectClaimBytes << 16) | usbdevfsType | 27u);
        Assert.Equal(255, LinuxUsbAPI.USBDEVFS_MAXDRIVERNAME);
        Assert.Equal(0x02u, LinuxUsbAPI.USBDEVFS_DISCONNECT_CLAIM_EXCEPT_DRIVER);
    }

    [Fact]
    public void DisconnectStructures_FieldOffsets_MatchKernelLayout()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<LinuxUsbAPI.Usbdevfs_ioctl>(nameof(LinuxUsbAPI.Usbdevfs_ioctl.Ifno)));
        Assert.Equal(4, (int)Marshal.OffsetOf<LinuxUsbAPI.Usbdevfs_ioctl>(nameof(LinuxUsbAPI.Usbdevfs_ioctl.IoctlCode)));
        Assert.Equal(8, (int)Marshal.OffsetOf<LinuxUsbAPI.Usbdevfs_ioctl>(nameof(LinuxUsbAPI.Usbdevfs_ioctl.Data)));
        Assert.Equal(16, Marshal.SizeOf<LinuxUsbAPI.Usbdevfs_ioctl>());

        Assert.Equal(0, (int)Marshal.OffsetOf<LinuxUsbAPI.Usbdevfs_disconnect_claim>(nameof(LinuxUsbAPI.Usbdevfs_disconnect_claim.Interface)));
        Assert.Equal(4, (int)Marshal.OffsetOf<LinuxUsbAPI.Usbdevfs_disconnect_claim>(nameof(LinuxUsbAPI.Usbdevfs_disconnect_claim.Flags)));
        Assert.Equal(264, Marshal.SizeOf<LinuxUsbAPI.Usbdevfs_disconnect_claim>());
    }

    [Fact]
    public void IsInterfaceDirectoryName_SysfsInterfaceAndDeviceNames_ClassifiesCorrectly()
    {
        Assert.True(LinuxSysfsTopology.IsInterfaceDirectoryName("1-5.3:1.0"));
        Assert.True(LinuxSysfsTopology.IsInterfaceDirectoryName("1-8:1.3"));
        Assert.False(LinuxSysfsTopology.IsInterfaceDirectoryName("1-5.3"));
        Assert.False(LinuxSysfsTopology.IsInterfaceDirectoryName("usb1"));
    }
}