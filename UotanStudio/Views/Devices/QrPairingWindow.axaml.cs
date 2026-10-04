using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using QRCoder;
using UotanStudio.Comm.Protocols.Adb.Pairing;
using UotanStudio.Services;
using UotanStudio.Services.Devices;

namespace UotanStudio.Views.Devices;

/// <summary>
/// Wireless-debugging QR pairing window: renders a fresh one-time QR code
/// (new random credentials on every open), waits for the phone to scan it,
/// runs the pairing and reports progress.
/// <para>无线调试扫码配对窗口：每次打开都渲染全新的一次性二维码（重新随机
/// 生成凭据），等待手机扫描并完成配对，过程中报告进度。</para>
/// </summary>
public partial class QrPairingWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private AdbQrPairingConnectPortException? _pendingConnect;

    public QrPairingWindow()
    {
        InitializeComponent();
        Closing += (_, _) => _cancellation.Cancel();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = RunPairingAsync();
    }

    private async Task RunPairingAsync()
    {
        var credentials = AdbQrPairing.GenerateCredentials();
        ShowQrCode(AdbQrPairing.BuildPayload(credentials));
        SetStatus("Device_QrPairWaiting");
        var progress = new Progress<QrPairingProgress>(Report);
        try
        {
            var outcome = await App.DeviceMonitor.RunQrPairingAsync(credentials, progress, _cancellation.Token)
                .ConfigureAwait(true);
            SetStatus("Device_QrPairDone", outcome.Address);
            await Task.Delay(1500, CancellationToken.None).ConfigureAwait(true);
            Close();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            SetStatus("Device_QrPairCanceled");
            ActionButton.Content = LanguageService.Instance.GetString("About_Close");
        }
        catch (AdbQrPairingConnectPortException ex)
        {
            _pendingConnect = ex;
            SetStatus("Device_QrPairManualNeeded", ex.Host);
            ManualPortPanel.IsVisible = true;
            PortBox.Focus();
        }
        catch (Exception ex)
        {
            SetStatus("Device_QrPairFailed", UserError.Describe(ex));
        }
    }

    private void Report(QrPairingProgress progress)
    {
        switch (progress.Stage)
        {
            case QrPairingStage.Pairing:
                SetStatus("Device_QrPairPairing", progress.Detail ?? string.Empty);
                break;
            case QrPairingStage.Connecting:
                SetStatus("Device_QrPairConnecting", progress.Detail ?? string.Empty);
                break;
            default:
                break;
        }
    }

    private void ShowQrCode(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        QrImage.Source = new Bitmap(new MemoryStream(png.GetGraphic(8)));
    }

    private void SetStatus(string key, params object[] args)
    {
        StatusText.Text = LanguageService.Instance.GetString(key, args);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_cancellation.IsCancellationRequested)
        {
            _cancellation.Cancel();
            return;
        }
        Close();
    }

    private async void OnManualConnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ConnectWithManualPortAsync();

    private async void OnManualPortKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ConnectWithManualPortAsync();
        }
    }

    private async Task ConnectWithManualPortAsync()
    {
        if (_pendingConnect is not { } pending || !ManualConnectButton.IsEnabled)
        {
            return;
        }
        if (!int.TryParse(PortBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            SetStatus("Device_QrPairManualInvalidPort");
            return;
        }

        ManualConnectButton.IsEnabled = false;
        PortBox.IsEnabled = false;
        try
        {
            var address = await App.DeviceMonitor
                .CompleteQrPairingWithPortAsync(pending.Route, pending.Host, port, CancellationToken.None)
                .ConfigureAwait(true);
            _pendingConnect = null;
            SetStatus("Device_QrPairDone", address);
            await Task.Delay(1500, CancellationToken.None).ConfigureAwait(true);
            Close();
        }
        catch (Exception ex)
        {
            SetStatus("Device_QrPairFailed", UserError.Describe(ex));
            ManualConnectButton.IsEnabled = true;
            PortBox.IsEnabled = true;
        }
    }
}