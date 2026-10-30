using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Transports.Usb.Backend;
using Xunit;

namespace UotanStudio.Tests.Usb;

public sealed class UsbConfigurationDescriptorParserTests
{
    private const byte ConfigurationDescriptorType = 2;
    private const byte InterfaceDescriptorType = 4;
    private const byte EndpointDescriptorType = 5;
    private const byte ClassSpecificInterfaceDescriptorType = 0x24;
    private const byte BulkAttributes = 0x02;
    private const ushort BulkMaxPacketSize = 512;
    private const ushort FullSpeedBcdUsb = 0x0110;

    [Fact]
    public void Parse_MassStorageInterfaceWithBulkPair_ReportsClassAndEndpointDirections()
    {
        var descriptor = Configuration(1,
            Interface(0, classCode: 0x08, subClass: 0x06, protocol: 0x50,
                Endpoint(0x02), Endpoint(0x81)));

        var interfaces = UsbConfigurationDescriptorParser.Parse(descriptor.AsSpan());

        var massStorage = Assert.Single(interfaces);
        Assert.Equal(0, massStorage.InterfaceNumber);
        Assert.Equal(0x08, massStorage.Class);
        Assert.Equal(0x06, massStorage.SubClass);
        Assert.Equal(0x50, massStorage.Protocol);
        Assert.Equal(2, massStorage.Endpoints.Count);
        Assert.False(massStorage.Endpoints[0].IsIn);
        Assert.Equal(0x02, massStorage.Endpoints[0].EndpointAddress);
        Assert.Equal(BulkAttributes, massStorage.Endpoints[0].Attributes);
        Assert.Equal(BulkMaxPacketSize, massStorage.Endpoints[0].MaxPacketSize);
        Assert.True(massStorage.Endpoints[1].IsIn);
        Assert.Equal(0x81, massStorage.Endpoints[1].EndpointAddress);
        Assert.Equal(BulkMaxPacketSize, massStorage.Endpoints[1].MaxPacketSize);
    }

    [Fact]
    public void Parse_MultipleInterfaces_KeepsDescriptorOrderAndPerInterfaceEndpoints()
    {
        var descriptor = Configuration(2,
            Interface(0, classCode: 0xFF, subClass: 0x42, protocol: 0x01,
                ClassSpecific(4), Endpoint(0x84)),
            Interface(2, classCode: 0x08, subClass: 0x06, protocol: 0x50,
                Endpoint(0x01), Endpoint(0x82)));

        var interfaces = UsbConfigurationDescriptorParser.Parse(descriptor.AsSpan());

        Assert.Equal(2, interfaces.Count);
        Assert.Equal(0, interfaces[0].InterfaceNumber);
        Assert.Equal(0xFF, interfaces[0].Class);
        Assert.Equal(0x84, Assert.Single(interfaces[0].Endpoints).EndpointAddress);
        Assert.Equal(2, interfaces[1].InterfaceNumber);
        Assert.Equal(2, interfaces[1].Endpoints.Count);
        Assert.Equal(0x01, interfaces[1].Endpoints[0].EndpointAddress);
        Assert.Equal(0x82, interfaces[1].Endpoints[1].EndpointAddress);
    }

    [Fact]
    public void Parse_InterfaceWithoutEndpoints_ReportsEmptyEndpointList()
    {
        var descriptor = Configuration(1, Interface(1, classCode: 0xFF, subClass: 0x00, protocol: 0x00));

        var interfaces = UsbConfigurationDescriptorParser.Parse(descriptor.AsSpan());

        Assert.Empty(Assert.Single(interfaces).Endpoints);
    }

    [Fact]
    public void Parse_EndpointDescriptorBeforeAnyInterface_IsIgnored()
    {
        var descriptor = Configuration(1,
            Endpoint(0x81),
            Interface(0, classCode: 0x08, subClass: 0x06, protocol: 0x50, Endpoint(0x02)));

        var interfaces = UsbConfigurationDescriptorParser.Parse(descriptor.AsSpan());

        Assert.Equal(0x02, Assert.Single(Assert.Single(interfaces).Endpoints).EndpointAddress);
    }

    [Fact]
    public void Parse_EndpointDeclaredBeyondBuffer_DropsTruncatedEndpointOnly()
    {
        var descriptor = Configuration(1,
            Interface(0, classCode: 0x08, subClass: 0x06, protocol: 0x50, Endpoint(0x81), Endpoint(0x02)));
        var truncated = descriptor.AsSpan(0, descriptor.Length - 3);

        var interfaces = UsbConfigurationDescriptorParser.Parse(truncated);

        Assert.Equal(0x81, Assert.Single(Assert.Single(interfaces).Endpoints).EndpointAddress);
    }

    [Fact]
    public void Parse_BufferShorterThanConfigurationHeader_ReturnsNoInterface()
    {
        var shortBuffer = Configuration(0).AsSpan(0, 8);

        Assert.Empty(UsbConfigurationDescriptorParser.Parse(shortBuffer));
    }

    [Fact]
    public void Parse_EmptyBuffer_ReturnsNoInterface()
        => Assert.Empty(UsbConfigurationDescriptorParser.Parse(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Parse_NegativeLength_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            UsbConfigurationDescriptorParser.Parse(new IntPtr(1), -1));

    [Fact]
    public void Parse_NullPointer_ReturnsNoInterface()
        => Assert.Empty(UsbConfigurationDescriptorParser.Parse(IntPtr.Zero, 64));

    [Fact]
    public void Parse_NativeBuffer_ReportsSameInterfacesAsManagedSpan()
    {
        var descriptor = Configuration(1,
            Interface(0, classCode: 0x08, subClass: 0x06, protocol: 0x50, Endpoint(0x02), Endpoint(0x81)));
        var buffer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, buffer, descriptor.Length);

            var interfaces = UsbConfigurationDescriptorParser.Parse(buffer, descriptor.Length);

            var massStorage = Assert.Single(interfaces);
            Assert.Equal(2, massStorage.Endpoints.Count);
            Assert.Equal(BulkMaxPacketSize, massStorage.Endpoints[1].MaxPacketSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] Configuration(byte interfaceCount, params byte[][] descriptors)
    {
        var total = 9 + descriptors.Sum(part => part.Length);
        var buffer = new byte[total];
        buffer[0] = 9;
        buffer[1] = ConfigurationDescriptorType;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), (ushort)total);
        buffer[4] = interfaceCount;
        buffer[5] = 1;
        buffer[6] = 0;
        buffer[7] = 0x80;
        buffer[8] = 50;
        var position = 9;
        foreach (var part in descriptors)
        {
            part.CopyTo(buffer, position);
            position += part.Length;
        }

        return buffer;
    }

    private static byte[] Interface(byte number, byte classCode, byte subClass, byte protocol, params byte[][] trailing)
    {
        var buffer = new byte[9];
        buffer[0] = 9;
        buffer[1] = InterfaceDescriptorType;
        buffer[2] = number;
        buffer[3] = 0;
        buffer[4] = (byte)trailing.Count(part => part.Length > 1 && part[1] == EndpointDescriptorType);
        buffer[5] = classCode;
        buffer[6] = subClass;
        buffer[7] = protocol;
        buffer[8] = 0;
        return [.. buffer, .. trailing.SelectMany(part => part)];
    }

    private static byte[] Endpoint(byte address, byte interval = 0)
    {
        var buffer = new byte[7];
        buffer[0] = 7;
        buffer[1] = EndpointDescriptorType;
        buffer[2] = address;
        buffer[3] = BulkAttributes;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), BulkMaxPacketSize);
        buffer[6] = interval;
        return buffer;
    }

    private static byte[] ClassSpecific(int length)
    {
        var buffer = new byte[length];
        buffer[0] = (byte)length;
        buffer[1] = ClassSpecificInterfaceDescriptorType;
        return buffer;
    }
}