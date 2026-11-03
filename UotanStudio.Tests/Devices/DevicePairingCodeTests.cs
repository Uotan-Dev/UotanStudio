using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class DevicePairingCodeTests
{
    [Theory]
    [InlineData("192.168.1.5:37123", "192.168.1.5", 37123)]
    [InlineData("192.168.1.5:5555", "192.168.1.5", 5555)]
    [InlineData("[fe80::1]:37123", "fe80::1", 37123)]
    public void TryNormalizePairingEndpoint_ExplicitEndpoint_IsAccepted(string input, string expectedHost,
        int expectedPort)
    {
        var accepted = DeviceMonitorService.TryNormalizePairingEndpoint(input, out var host, out var port);

        Assert.True(accepted);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.5")]
    [InlineData("bad host:37123")]
    [InlineData(":37123")]
    [InlineData("192.168.1.5:0")]
    [InlineData("192.168.1.5:70000")]
    public void TryNormalizePairingEndpoint_BareOrInvalidEndpoint_IsRejected(string input)
        => Assert.False(DeviceMonitorService.TryNormalizePairingEndpoint(input, out _, out _));

    [Theory]
    [InlineData("not a valid address", "")]
    [InlineData("192.168.1.5", "123456")]
    public async Task ConnectRemoteCommand_InvalidAddress_ShowsDialogWithoutConnecting(string address, string code)
    {
        var errors = new List<string>();
        var connects = 0;
        using var viewModel = new DeviceViewModel(monitorDevices: false,
            changeNetworkConnection: (_, _, _, _, _) =>
            {
                connects++;
                return Task.CompletedTask;
            },
            showDeviceError: message =>
            {
                errors.Add(message);
                return Task.CompletedTask;
            });
        viewModel.RemoteAddress = address;
        viewModel.RemotePairingCode = code;

        await viewModel.ConnectRemoteCommand.ExecuteAsync(null);

        Assert.Single(errors);
        Assert.Equal(0, connects);
        Assert.False(viewModel.IsConnectionBusy);
    }

    [Fact]
    public async Task ConnectRemoteCommand_ConnectionFails_ShowsErrorAndClearsBusyState()
    {
        var errors = new List<string>();
        using var viewModel = new DeviceViewModel(monitorDevices: false,
            changeNetworkConnection: (_, _, _, _, _) => Task.FromException(new IOException("connection refused")),
            showDeviceError: message =>
            {
                errors.Add(message);
                return Task.CompletedTask;
            });
        viewModel.RemoteAddress = "192.168.1.5:5555";

        await viewModel.ConnectRemoteCommand.ExecuteAsync(null);

        Assert.Contains("192.168.1.5:5555", Assert.Single(errors));
        Assert.False(viewModel.IsDeviceOperationBusy);
    }

    [Fact]
    public async Task ConnectRemoteCommand_Canceled_DoesNotShowError()
    {
        var errors = new List<string>();
        using var viewModel = new DeviceViewModel(monitorDevices: false,
            changeNetworkConnection: (_, _, _, _, token) => Task.Delay(Timeout.Infinite, token),
            showDeviceError: message =>
            {
                errors.Add(message);
                return Task.CompletedTask;
            });
        viewModel.RemoteAddress = "192.168.1.5:5555";

        var connection = viewModel.ConnectRemoteCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsDeviceOperationBusy);
        viewModel.CancelConnectionCommand.Execute(null);
        await connection.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(errors);
        Assert.False(viewModel.IsDeviceOperationBusy);
    }

    [Fact]
    public async Task PairWithCodeAsync_BlankArguments_AreRejectedBeforeConnecting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => AdbQrPairingService.PairWithCodeAsync(
            " ", 45678, "123456", cancellationToken: cancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AdbQrPairingService.PairWithCodeAsync(
            "192.0.2.7", 45678, " ", cancellationToken: cancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AdbQrPairingService.PairWithCodeAsync(
            "192.0.2.7", 45678, "12:34", cancellationToken: cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AdbQrPairingService.PairWithCodeAsync(
            "192.0.2.7", 0, "123456", cancellationToken: cancellationToken));
    }

    [Fact]
    public void PairingCode_WithoutExplicitPairingPort_ReportsInvalidAddress()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        viewModel.RemoteAddress = "192.168.1.5";
        Assert.False(viewModel.HasInvalidRemoteAddress);

        viewModel.RemotePairingCode = "123456";

        Assert.True(viewModel.HasPairingCode);
        Assert.True(viewModel.HasInvalidRemoteAddress);
        Assert.True(viewModel.ConnectRemoteCommand.CanExecute(null));
    }

    [Fact]
    public void PairingCode_WithoutPortSwitchingProtocol_ClearsPairingState()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false);
        viewModel.RemoteAddress = "192.168.1.5:37123";
        viewModel.RemotePairingCode = "123456";
        Assert.True(viewModel.HasPairingCode);

        viewModel.RemoteProtocolIndex = 1;

        Assert.False(viewModel.HasPairingCode);
        Assert.False(viewModel.HasInvalidRemoteAddress);
    }

    [Fact]
    public async Task ConnectRemoteCommand_WithPairingCode_PairsThenStoresConnectedAddress()
    {
        string? pairedAddress = null;
        string? pairedCode = null;
        var plainConnects = 0;
        using var viewModel = new DeviceViewModel(monitorDevices: false,
            changeNetworkConnection: (_, _, _, _, _) =>
            {
                plainConnects++;
                return Task.CompletedTask;
            },
            pairWithCode: (address, code, _, _) =>
            {
                pairedAddress = address;
                pairedCode = code;
                return Task.FromResult("192.168.1.5:41235");
            });
        viewModel.RemoteAddress = "192.168.1.5:37123";
        viewModel.RemotePairingCode = "123456";

        await viewModel.ConnectRemoteCommand.ExecuteAsync(null);

        Assert.Equal("192.168.1.5:37123", pairedAddress);
        Assert.Equal("123456", pairedCode);
        Assert.Equal(0, plainConnects);
        Assert.Equal("192.168.1.5:41235", viewModel.RemoteAddress);
        Assert.Empty(viewModel.RemotePairingCode);
        Assert.False(viewModel.IsConnectionBusy);
    }

    [Fact]
    public async Task ConnectRemoteCommand_WithoutPairingCode_UsesPlainConnect()
    {
        var pairs = 0;
        var connects = 0;
        using var viewModel = new DeviceViewModel(monitorDevices: false,
            changeNetworkConnection: (_, _, _, _, _) =>
            {
                connects++;
                return Task.CompletedTask;
            },
            pairWithCode: (_, _, _, _) =>
            {
                pairs++;
                return Task.FromResult("192.168.1.5:5555");
            });
        viewModel.RemoteAddress = "192.168.1.5:5555";

        await viewModel.ConnectRemoteCommand.ExecuteAsync(null);

        Assert.Equal(1, connects);
        Assert.Equal(0, pairs);
    }

    [Fact]
    public async Task ConnectRemoteCommand_WhenPairingFails_KeepsTheCodeForRetry()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false,
            pairWithCode: (_, _, _, _) =>
                Task.FromException<string>(new IOException("The pairing password is wrong or has expired.")));
        viewModel.RemoteAddress = "192.168.1.5:37123";
        viewModel.RemotePairingCode = "123456";

        await viewModel.ConnectRemoteCommand.ExecuteAsync(null);

        Assert.Equal("123456", viewModel.RemotePairingCode);
        Assert.False(viewModel.IsConnectionBusy);
    }

    [Fact]
    public async Task ConnectRemoteCommand_WhenPairedButNoConnectPort_ClearsTheCodeAndPrefillsTheSuggestion()
    {
        using var viewModel = new DeviceViewModel(monitorDevices: false,
            pairWithCode: (_, _, _, _) => Task.FromException<string>(
                new AdbQrPairingConnectPortException(AdbQrPairingRoute.Direct, "192.168.1.5",
                    "no secure connect port answered.", 41235)));
        viewModel.RemoteAddress = "192.168.1.5:37123";
        viewModel.RemotePairingCode = "123456";

        await viewModel.ConnectRemoteCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.RemotePairingCode);
        Assert.Equal("192.168.1.5:41235", viewModel.RemoteAddress);
        Assert.False(viewModel.IsConnectionBusy);
    }
}