using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Pins the boot handshake contract: the strict Boot ROM path is a single pass, the relaxed path
/// retries a bounded number of times, and every mismatch faults the session without any retry leak.
/// </summary>
public sealed class MtkBootHandshakeTests
{
    [Fact]
    public async Task StrictPathSendsComplementedBytesAndReachesBootloader()
    {
        var device = new MtkBootScriptDevice();
        using var session = new MtkSession(device.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);

        var info = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Equal(new byte[] { 0xA0, 0x0A, 0x50, 0x05 }, device.HandshakeReceived);
        Assert.Equal(0x0CA3, info.HardwareCode);
        Assert.Equal(0x0002, info.HardwareSubCode);
        Assert.Equal(0xCA00, info.HardwareVersion);
        Assert.Equal(0x0003, info.SoftwareVersion);
    }

    [Fact]
    public async Task StrictPathRecoversAfterOneRejectedAttempt()
    {
        var device = new MtkBootScriptDevice { RejectedHandshakeAttempts = 1 };
        using var session = new MtkSession(device.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);

        var info = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Equal(0x0CA3, info.HardwareCode);
        Assert.True(device.HandshakeReceived.Count > 4);
    }

    /// <remarks>
    /// Each attempt aborts on its first rejected echo, so only the leading 0xA0 of the handshake is sent per attempt;
    /// five rejected attempts therefore leave exactly five bytes.
    /// <para>每次尝试都在第一个被拒的回显处中止，因此每次尝试只发出握手中前导的 0xA0；五次被拒尝试恰好留下五个字节。</para>
    /// </remarks>
    [Fact]
    public async Task StrictPathFaultsAfterFiveRejectedAttempts()
    {
        var device = new MtkBootScriptDevice { RejectedHandshakeAttempts = 50 };
        using var session = new MtkSession(device.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);

        await Assert.ThrowsAsync<MtkProtocolException>(
            () => session.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);

        Assert.Equal(5, device.HandshakeReceived.Count);
    }

    [Fact]
    public async Task RelaxedPathRetriesUntilSynchronized()
    {
        var device = new MtkBootScriptDevice { RejectedHandshakeAttempts = 1 };
        using var session = new MtkSession(device.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);
        session.NoteUsbDescriptor(new UsbDeviceInfo { VendorId = 0x0E8D, ProductId = 0x2000 });

        var info = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Equal(0x0CA3, info.HardwareCode);
        Assert.True(device.HandshakeReceived.Count > 4);
    }

    /// <remarks>
    /// The relaxed path warms the port with one bare 0xA0 and then makes five attempts, each of which aborts on the
    /// first rejected echo before the remaining three bytes are sent, so the count is 1 + 5.
    /// <para>宽松路径先发一个裸 0xA0 预热端口，再进行五次尝试；每次尝试都在第一个被拒的回显处中止、其余三个字节尚未发出，
    /// 因此计数为 1 + 5。</para>
    /// </remarks>
    [Fact]
    public async Task RelaxedPathGivesUpAfterFiveAttempts()
    {
        var device = new MtkBootScriptDevice { RejectedHandshakeAttempts = 50 };
        using var session = new MtkSession(device.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);
        session.NoteUsbDescriptor(new UsbDeviceInfo { VendorId = 0x0E8D, ProductId = 0x2000 });

        await Assert.ThrowsAsync<MtkProtocolException>(
            () => session.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);

        Assert.Equal(1 + 5, device.HandshakeReceived.Count);
    }

    [Fact]
    public async Task TargetConfigurationStatusIsChecked()
    {
        var device = new MtkBootScriptDevice { TargetConfigurationStatus = 0x1000 };
        using var session = new MtkSession(device.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);

        var error = await Assert.ThrowsAsync<MtkProtocolException>(
            () => session.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0x1000u, error.Status);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task IdentityBlobsAreReportedWhenTheTargetAnswers()
    {
        var device = new MtkBootScriptDevice { Meid = [0x11, 0x22, 0x33], Socid = [0x44, 0x55, 0x66, 0x77] };
        using var session = new MtkSession(device.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);

        var info = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, info.Meid);
        Assert.Equal(new byte[] { 0x44, 0x55, 0x66, 0x77 }, info.Socid);
        Assert.Equal((byte?)0xFE, info.BlVersion);
        Assert.Equal((byte?)0x05, info.BromVersion);
    }

    [Fact]
    public async Task IdentityQueryFailureLeavesSessionUsable()
    {
        var device = new MtkBootScriptDevice { BootLoaderVersion = 0x00, Meid = [], Socid = [] };
        using var session = new MtkSession(device.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);

        var info = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
        Assert.Null(info.Meid);
        Assert.Null(info.Socid);
    }
}