using System.Buffers.Binary;
using UotanStudio.Comm.Transports.Usb.Backend.Linux;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports.Usb;

public sealed class LinuxUsbDescriptorTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(Skip = "Linux libc positional reads are required.", SkipUnless = nameof(IsLinux))]
    public async Task Read_NativeFileDescriptor_PreservesCompleteRepeatedReads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-usb-descriptor-{Guid.NewGuid():N}");
        var bytes = Device(Configuration(4096), Configuration(64));
        try
        {
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var fd = LinuxUsbAPI.open(path, LinuxUsbAPI.O_CLOEXEC);
            Assert.True(fd >= 0);
            try
            {
                Assert.Equal(bytes, LinuxUsbDescriptors.Read(fd));
                Assert.Equal(bytes, LinuxUsbDescriptors.Read(fd));
            }
            finally
            {
                LinuxUsbAPI.close(fd);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(65535)]
    public void Read_ConfigurationSize_ReadsCompleteConfigurationAtExplicitOffset(int length)
    {
        var bytes = Device(Configuration(length));
        var calls = new List<(int Offset, int Length)>();

        var result = LinuxUsbDescriptors.Read((offset, count) =>
        {
            calls.Add((offset, count));
            return bytes.AsSpan(offset, count).ToArray();
        });

        Assert.Equal(bytes, result);
        Assert.Equal([(0, 18), (18, 9), (18, length)], calls);
    }

    [Fact]
    public void Read_MultipleConfigurations_PreservesAllConfigurationBytes()
    {
        var bytes = Device(Configuration(4096), Configuration(64));
        var calls = new List<(int Offset, int Length)>();

        var result = LinuxUsbDescriptors.Read((offset, count) =>
        {
            calls.Add((offset, count));
            return bytes.AsSpan(offset, count).ToArray();
        });

        Assert.Equal(bytes, result);
        Assert.Equal([(0, 18), (18, 9), (18, 4096), (4114, 9), (4114, 64)], calls);
    }

    [Theory]
    [InlineData("short-device")]
    [InlineData("device-type")]
    [InlineData("device-size")]
    [InlineData("zero-configs")]
    [InlineData("short-config-header")]
    [InlineData("config-type")]
    [InlineData("config-size")]
    [InlineData("config-total")]
    [InlineData("short-config")]
    [InlineData("changed-header")]
    public void Read_MalformedOrChangingDescriptors_RejectsIncompleteSnapshot(string scenario)
    {
        var bytes = Device(Configuration(32));
        switch (scenario)
        {
            case "device-type": bytes[1] = 2; break;
            case "device-size": bytes[0] = 17; break;
            case "zero-configs": bytes[17] = 0; break;
            case "config-type": bytes[19] = 1; break;
            case "config-size": bytes[18] = 8; break;
            case "config-total": bytes[20] = 8; break;
        }

        var result = LinuxUsbDescriptors.Read((offset, count) =>
        {
            if (scenario == "short-device" && offset == 0
                || scenario == "short-config-header" && offset == 18 && count == 9
                || scenario == "short-config" && count == 32)
            {
                return bytes.AsSpan(offset, count - 1).ToArray();
            }
            var data = bytes.AsSpan(offset, count).ToArray();
            if (scenario == "changed-header" && count == 32)
            {
                data[5]++;
            }
            return data;
        });

        Assert.Null(result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TryParseDescriptor_EndpointsOnDifferentInterfaces_DoesNotCombineEndpoints(byte declaredCount)
    {
        byte[] body =
        [
            9, 4, 0, 0, declaredCount, 255, 0, 0, 0,
            7, 5, 0x81, 2, 64, 0, 0,
            9, 4, 1, 0, 1, 255, 0, 0, 0,
            7, 5, 0x02, 2, 64, 0, 0
        ];
        var config = Configuration(9 + body.Length);
        body.CopyTo(config, 9);
        var descriptor = Device(config);

        Assert.Null(LinuxUsbFinder.TryParseDescriptor(descriptor, descriptor.Length, null));
    }

    [Fact]
    public void TryParseDescriptor_OneCompleteInterface_SelectsItsEndpointPair()
    {
        byte[] body = [9, 4, 0, 0, 2, 255, 0, 0, 0, 7, 5, 0x81, 2, 64, 0, 0, 7, 5, 0x02, 2, 64, 0, 0];
        var config = Configuration(9 + body.Length);
        body.CopyTo(config, 9);
        var descriptor = Device(config);

        var result = LinuxUsbFinder.TryParseDescriptor(descriptor, descriptor.Length, null);

        Assert.NotNull(result);
        Assert.Equal(0x81, result.EndpointIn);
        Assert.Equal(0x02, result.EndpointOut);
        Assert.Null(LinuxUsbFinder.TryParseDescriptor(descriptor, descriptor.Length + 1, null));
    }

    private static byte[] Configuration(int length)
    {
        var result = new byte[length];
        result[0] = 9;
        result[1] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), (ushort)length);
        result[5] = 1;
        return result;
    }

    private static byte[] Device(params byte[][] configurations)
    {
        var result = new byte[18 + configurations.Sum(config => config.Length)];
        result[0] = 18;
        result[1] = 1;
        result[17] = (byte)configurations.Length;
        var offset = 18;
        foreach (var config in configurations)
        {
            config.CopyTo(result, offset);
            offset += config.Length;
        }
        return result;
    }
}