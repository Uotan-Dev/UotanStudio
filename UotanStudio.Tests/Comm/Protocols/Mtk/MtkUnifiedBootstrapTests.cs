using System.Text;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Firmware.VendorFirmware.MediaTek;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnifiedBootstrapAsync_XmlScript_CompletesSelectedStorageAfterSla(bool ufs, bool sla)
    {
        using var dir = new TempDirectory();
        var response = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        var responder = new DaSlaResponder((_, _, _, _) => Task.FromResult(response));
        var store = new RecordingXmlHostFileStore();
        var options = XmlBootstrapOptions(dir) with
        {
            StorageKind = ufs ? MtkXFlashStorageKind.Ufs : MtkXFlashStorageKind.Emmc,
            DaSlaResponder = responder,
            Xml = XmlBootstrapPolicy() with { HostFileStore = store, MaximumHostFileTransferBytes = 1024 }
        };
        using var transport = new ScriptedCommTransport();
        EnqueueXmlBootstrap(transport, ufs, sla);

        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Xml, options,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        Assert.Equal(ufs ? MtkXmlStorageKind.Ufs : MtkXmlStorageKind.Emmc, session.XmlStorage!.Kind);
        Assert.Null(session.XFlashStorage);
        Assert.Same(store, session.HostFileStore);
        Assert.Equal(1024, session.MaximumHostFileTransferBytes);
        Assert.Equal(sla ? 1 : 0, responder.Calls);
        Assert.Equal(0, transport.DisposeCount);
        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(Enumerable.Repeat((byte)0x42, 12)));
        var commands = XmlBootstrapCommands(transport);
        Assert.Equal(new[]
        {
            "CMD:SET-RUNTIME-PARAMETER", "CMD:HOST-SUPPORTED-COMMANDS", "CMD:NOTIFY-INIT-HW",
            "CMD:SET-HOST-INFO", "CMD:HOST-SUPPORTED-COMMANDS", "CMD:BOOT-TO",
            "CMD:HOST-SUPPORTED-COMMANDS", "CMD:NOTIFY-INIT-HW"
        }, commands.Take(8));
        Assert.Equal("CMD:GET-HW-INFO", commands.Last());
        Assert.DoesNotContain(commands, command => command.Contains("WRITE", StringComparison.Ordinal));
        using var exclusive = File.Open(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task UnifiedBootstrapAsync_ExplicitXFlash_PreservesStartupSequence()
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        EnqueueBootstrap(transport, false);

        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.XFlash, BootstrapOptions(dir),
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData("missing-policy")]
    [InlineData("runtime")]
    [InlineData("runtime-enum")]
    [InlineData("envelope")]
    [InlineData("envelope-length")]
    [InlineData("host")]
    [InlineData("host-length")]
    [InlineData("deadline")]
    [InlineData("limit")]
    [InlineData("emi")]
    [InlineData("protocol")]
    [InlineData("xflash-policy")]
    public async Task UnifiedBootstrapAsync_InvalidPolicy_RejectsBeforeIoOrSerialOpen(string failure)
    {
        using var dir = new TempDirectory();
        var xml = XmlBootstrapPolicy();
        var options = XmlBootstrapOptions(dir);
        var protocol = MtkDaProtocol.Xml;
        switch (failure)
        {
            case "missing-policy": options = options with { Xml = null }; break;
            case "runtime": options = options with { Xml = xml with { Runtime = null! } }; break;
            case "runtime-enum": options = options with { Xml = xml with { Runtime = xml.Runtime with { HostOs = (MtkXmlHostOs)999 } } }; break;
            case "envelope": options = options with { Xml = xml with { SlaEnvelopeName = "a:b" } }; break;
            case "envelope-length": options = options with { Xml = xml with { SlaEnvelopeName = new string('a', 129) } }; break;
            case "host": options = options with { Xml = xml with { HostInfo = "\0" } }; break;
            case "host-length": options = options with { Xml = xml with { HostInfo = new string('a', 257) } }; break;
            case "deadline": options = options with { Xml = xml with { OperationTimeoutMs = 86400001 } }; break;
            case "limit": options = options with { Xml = xml with { MaximumHostFileTransferBytes = 0 } }; break;
            case "emi":
                using (var block = new MemoryStream("MTK_BLOADER_INFO_v12parameters"u8.ToArray()))
                {
                    options = options with
                    {
                        EmiParameters = MtkEmiReader.ReadBlock(block, 0, (int)block.Length,
                            MtkEmiPayloadLayout.BootloaderInfo, TestContext.Current.CancellationToken)
                    };
                }
                break;
            case "protocol": protocol = (MtkDaProtocol)999; break;
            case "xflash-policy": protocol = MtkDaProtocol.XFlash; break;
        }
        using var transport = new ScriptedCommTransport();
        await Assert.ThrowsAnyAsync<Exception>(() => MtkBootstrap.InitializeAsync(transport, protocol, options,
            TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
        Assert.Equal(1, transport.DisposeCount);
        var opens = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => MtkBootstrap.ConnectSerialCoreAsync(
            ExplicitSerial(), protocol, options, (_, _) =>
            {
                opens++;
                return Task.FromResult<ICommTransport>(transport);
            }, TestContext.Current.CancellationToken));
        Assert.Equal(0, opens);
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("sla")]
    [InlineData("start")]
    [InlineData("cancel")]
    public async Task UnifiedBootstrapAsync_XmlFailure_ClosesWithoutProtocolFallback(string failure)
    {
        using var dir = new TempDirectory();
        var options = XmlBootstrapOptions(dir);
        using var transport = new ScriptedCommTransport();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (failure == "start")
        {
            EnqueueXmlBootstrapBoot(transport);
            EnqueueXmlText(transport, XmlControl("END", "OK"));
        }
        else
        {
            EnqueueXmlBootstrap(transport, ufs: failure == "storage", sla: failure == "sla");
        }
        if (failure == "cancel")
        {
            transport.OnWrite = bytes =>
            {
                if (Encoding.UTF8.GetString(bytes).Contains("CMD:SET-RUNTIME-PARAMETER", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }
                return [];
            };
        }

        await Assert.ThrowsAnyAsync<Exception>(() => MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Xml,
            options, cancellation.Token));

        Assert.Equal(1, transport.DisposeCount);
        Assert.DoesNotContain(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 0xC0 }));
        if (failure == "sla")
        {
            Assert.DoesNotContain("CMD:GET-HW-INFO", XmlBootstrapCommands(transport));
        }
        using var exclusive = File.Open(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task UnifiedBootstrapAsync_XmlSerial_RetainsLoaderUntilOpenAndReturnsOwnedSession()
    {
        using var dir = new TempDirectory();
        var options = XmlBootstrapOptions(dir);
        using var transport = new ScriptedCommTransport();
        EnqueueXmlBootstrap(transport, false, false);
        var opens = 0;

        var session = await MtkBootstrap.ConnectSerialCoreAsync(ExplicitSerial(), MtkDaProtocol.Xml, options, (_, _) =>
        {
            opens++;
            Assert.Throws<IOException>(() => File.Open(options.LoaderPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            return Task.FromResult<ICommTransport>(transport);
        }, TestContext.Current.CancellationToken);
        using (session)
        {
            Assert.Equal(1, opens);
            Assert.Equal(0, transport.DisposeCount);
            Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        }
        Assert.Equal(1, transport.DisposeCount);
    }

    private static MtkBootstrapOptions XmlBootstrapOptions(TempDirectory dir) => new()
    {
        LoaderPath = dir.WriteBytes("loader.bin", BootstrapContainer()),
        EntryIndex = 0,
        StageOneRegionIndex = 0,
        StageTwoRegionIndex = 1,
        StageTwoContent = MtkDaRegionContent.WithoutSignatureSuffix,
        StorageKind = MtkXFlashStorageKind.Emmc,
        Xml = XmlBootstrapPolicy()
    };

    private static MtkXmlBootstrapOptions XmlBootstrapPolicy() => new()
    {
        Runtime = XmlRuntimeOptions(),
        SlaEnvelopeName = "test_properties",
        HostInfo = "UotanStudio test"
    };

    private static void EnqueueXmlBootstrapBoot(ScriptedCommTransport transport)
    {
        EnqueueIdentity(transport);
        transport.Enqueue(Convert.FromHexString("D7002000000000000800000002000008000000D5002000000000"));
    }

    private static void EnqueueXmlBootstrap(ScriptedCommTransport transport, bool ufs, bool sla)
    {
        EnqueueXmlBootstrapBoot(transport);
        EnqueueXmlText(transport, XmlControl("START"));
        for (var index = 0; index < 4; index++)
        {
            EnqueueXmlCompletion(transport);
        }
        EnqueueXmlUpload(transport, 12, 12);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlStorage(transport, XmlPropertyDocument("DA.SLA", sla ? "ENABLED" : "DISABLED"));
        if (sla)
        {
            EnqueueXmlStorage(transport, XmlSlaDocument());
            EnqueueXmlSecurityResource(transport, 32, 16);
        }
        EnqueueXmlStorage(transport, XmlHardwareDocument(ufs));
    }

    private static string[] XmlBootstrapCommands(ScriptedCommTransport transport) =>
        transport.Writes.Select(bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'))
            .Where(text => text.Contains("<command>CMD:", StringComparison.Ordinal))
            .Select(text => System.Xml.Linq.XElement.Parse(text).Element("command")!.Value).ToArray();
}