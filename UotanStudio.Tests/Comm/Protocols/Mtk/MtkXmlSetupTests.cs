using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkXmlChecksumLevel.None, MtkXmlLogLevel.Trace, MtkXmlLogChannel.Uart, MtkXmlBatterySetting.AutoDetect, MtkXmlHostOs.Linux, false,
        "NONE", "TRACE", "UART", "AUTO-DETECT", "LINUX", "NO")]
    [InlineData(MtkXmlChecksumLevel.Usb, MtkXmlLogLevel.Debug, MtkXmlLogChannel.Usb, MtkXmlBatterySetting.Present, MtkXmlHostOs.Windows, true,
        "USB", "DEBUG", "USB", "YES", "WINDOWS", "YES")]
    [InlineData(MtkXmlChecksumLevel.Storage, MtkXmlLogLevel.Info, MtkXmlLogChannel.Both, MtkXmlBatterySetting.Absent, MtkXmlHostOs.Linux, false,
        "STORAGE", "INFO", "BOTH", "NO", "LINUX", "NO")]
    [InlineData(MtkXmlChecksumLevel.UsbAndStorage, MtkXmlLogLevel.Warn, MtkXmlLogChannel.Uart, MtkXmlBatterySetting.AutoDetect, MtkXmlHostOs.Windows, true,
        "USB-STORAGE", "WARN", "UART", "AUTO-DETECT", "WINDOWS", "YES")]
    [InlineData(MtkXmlChecksumLevel.None, MtkXmlLogLevel.Error, MtkXmlLogChannel.Usb, MtkXmlBatterySetting.Absent, MtkXmlHostOs.Linux, false,
        "NONE", "ERROR", "USB", "NO", "LINUX", "NO")]
    public async Task ConfigureXmlRuntimeAsync_ExplicitOptions_EmitsExactTokensAndAdvancesAfterCompletion(
        MtkXmlChecksumLevel checksum, MtkXmlLogLevel log, MtkXmlLogChannel channel, MtkXmlBatterySetting battery,
        MtkXmlHostOs host, bool dram, string checksumToken, string logToken, string channelToken,
        string batteryToken, string hostToken, string dramToken)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlControlSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        var before = transport.Writes.Count;
        var options = new MtkXmlRuntimeOptions
        {
            Checksum = checksum,
            LogLevel = log,
            LogChannel = channel,
            Battery = battery,
            HostOs = host,
            InitializeDram = dram
        };

        await session.ConfigureXmlRuntimeAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(6, transport.Writes.Count - before);
        var request = XDocument.Parse(Encoding.UTF8.GetString(transport.Writes[before + 1]).TrimEnd('\0')).Root!;
        Assert.Equal("1.1", request.Element("version")!.Value);
        Assert.Equal("CMD:SET-RUNTIME-PARAMETER", request.Element("command")!.Value);
        var arg = request.Element("arg")!;
        Assert.Equal(checksumToken, arg.Element("checksum_level")!.Value);
        Assert.Equal(logToken, arg.Element("da_log_level")!.Value);
        Assert.Equal(channelToken, arg.Element("log_channel")!.Value);
        Assert.Equal(batteryToken, arg.Element("battery_exist")!.Value);
        Assert.Equal(hostToken, arg.Element("system_os")!.Value);
        Assert.Equal(dramToken, request.Element("adv")!.Element("initialize_dram")!.Value);
        Assert.Equal(MtkSessionState.XmlRuntimeConfigured, session.State);
        Assert.Null(session.XFlashStorage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConfigureXmlRuntimeAsync(
            options, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(6, transport.Writes.Count - before);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("host")]
    [InlineData("battery")]
    [InlineData("level")]
    [InlineData("channel")]
    public async Task ConfigureXmlRuntimeAsync_InvalidEnum_RejectsBeforeIo(string field)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlControlSessionAsync(transport);
        var options = XmlRuntimeOptions();
        options = field switch
        {
            "checksum" => options with { Checksum = (MtkXmlChecksumLevel)99 },
            "host" => options with { HostOs = (MtkXmlHostOs)99 },
            "battery" => options with { Battery = (MtkXmlBatterySetting)99 },
            "level" => options with { LogLevel = (MtkXmlLogLevel)99 },
            _ => options with { LogChannel = (MtkXmlLogChannel)99 }
        };
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ConfigureXmlRuntimeAsync(
            options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlControlReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigureXmlRuntimeAsync_MissingCompletionOrCallback_DoesNotAdvance(bool callback)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlControlSessionAsync(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, callback ? XmlControl("DOWNLOAD-FILE") : XmlControl("END", "OK"));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ConfigureXmlRuntimeAsync(
            XmlRuntimeOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Equal(callback ? 2 : 4, transport.Writes.Count - before);
    }

    [Fact]
    public async Task InitializeXmlHardwareAsync_CompletePair_HoldsLeaseAndPreservesHostInfoAfterSetup()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        var before = transport.Writes.Count;
        Task? queued = null;
        transport.OnWrite = _ =>
        {
            queued ??= session.SetXmlHostInfoAsync("after hardware", TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return [];
        };

        await session.InitializeXmlHardwareAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await queued;

        var commands = transport.Writes.Skip(before).Where(bytes => bytes[0] == '<')
            .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes).TrimEnd('\0')).Root!).ToArray();
        Assert.Equal(new[] { "CMD:HOST-SUPPORTED-COMMANDS", "CMD:NOTIFY-INIT-HW", "CMD:SET-HOST-INFO" },
            commands.Select(command => command.Element("command")!.Value));
        Assert.Equal("CMD:PROGRESS-REPORT^1@", commands[0].Element("arg")!.Element("host_capability")!.Value);
        Assert.Equal(MtkSessionState.XmlHardwareInitialized, session.State);
        Assert.Null(session.XFlashStorage);
        Assert.Equal(18, transport.Writes.Count - before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeXmlHardwareAsync_CommandRejected_StopsAndFaults(bool second)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        if (second)
        {
            EnqueueXmlCompletion(transport);
        }
        EnqueueXmlText(transport, "ERR!UNSUPPORTED");
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(second ? 8 : 2, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task InitializeXmlHardwareAsync_RuntimeNotConfigured_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlControlSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlControlReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigureXmlRuntimeAsync_Cancellation_DistinguishesPreIoAndUncertainConfiguration(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlControlSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (inFlight)
        {
            transport.OnWrite = _ =>
            {
                cancellation.Cancel();
                return [];
            };
        }
        else
        {
            cancellation.Cancel();
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConfigureXmlRuntimeAsync(
            XmlRuntimeOptions(), cancellationToken: cancellation.Token));

        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count - before);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.XmlControlReady, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task XmlSetupAsync_InvalidDeadline_RejectsBeforeIo(int timeoutMs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ConfigureXmlRuntimeAsync(
            XmlRuntimeOptions(), timeoutMs, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.InitializeXmlHardwareAsync(
            timeoutMs, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    private static MtkXmlRuntimeOptions XmlRuntimeOptions() => new()
    {
        Checksum = MtkXmlChecksumLevel.UsbAndStorage,
        HostOs = MtkXmlHostOs.Windows,
        InitializeDram = false
    };

    private static void EnqueueXmlCompletion(ScriptedCommTransport transport)
    {
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
    }

    private static async Task<MtkSession> CreateXmlRuntimeSessionAsync(ScriptedCommTransport transport)
    {
        var session = await CreateXmlControlSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        await session.ConfigureXmlRuntimeAsync(XmlRuntimeOptions(), cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }
}