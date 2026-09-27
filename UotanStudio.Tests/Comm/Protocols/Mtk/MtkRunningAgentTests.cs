using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachRunningAgentAsync_DisconnectedTransport_RejectsBeforeIo(bool xml)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        transport.Disconnect();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (xml)
            {
                await session.AttachRunningXmlAgentAsync(cancellationToken: TestContext.Current.CancellationToken);
            }
            else
            {
                await session.AttachRunningXFlashAgentAsync(MtkXFlashStorageKind.Emmc,
                    cancellationToken: TestContext.Current.CancellationToken);
            }
        });

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
        Assert.Equal(MtkSessionState.Created, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachRunningAgentAsync_MissingFinalCompletion_DoesNotPublishGeometry(bool xml)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        if (xml)
        {
            EnqueueXmlStorage(transport, XmlHardwareDocument(false), finish: false);
        }
        else
        {
            EnqueueControlReply(transport, StorageGeometry(MtkXFlashStorageKind.Emmc));
            EnqueueXFlashFrame(transport, new byte[4]);
            EnqueueXFlashFrame(transport, new byte[4]);
            EnqueueXFlashFrame(transport, PacketLengths());
        }

        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            if (xml)
            {
                await session.AttachRunningXmlAgentAsync(cancellationToken: TestContext.Current.CancellationToken);
            }
            else
            {
                await session.AttachRunningXFlashAgentAsync(MtkXFlashStorageKind.Emmc,
                    cancellationToken: TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
        Assert.Null(session.XFlashStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachRunningAgentAsync_FollowedByRead_UsesFreshChannelAndGeometry(bool xml)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        var bytes = Enumerable.Range(0, 512).Select(index => (byte)index).ToArray();
        using var destination = new MemoryStream();
        if (xml)
        {
            EnqueueXmlStorage(transport, XmlHardwareDocument(false));
            await session.AttachRunningXmlAgentAsync(cancellationToken: TestContext.Current.CancellationToken);
            EnqueueXmlRead(transport, bytes);
            await session.ReadXmlStorageAsync("EMMC-USER", 0, bytes.Length, destination,
                cancellationToken: TestContext.Current.CancellationToken);
        }
        else
        {
            EnqueueControlReply(transport, StorageGeometry(MtkXFlashStorageKind.Emmc));
            EnqueueControlReply(transport, PacketLengths());
            await session.AttachRunningXFlashAgentAsync(MtkXFlashStorageKind.Emmc,
                cancellationToken: TestContext.Current.CancellationToken);
            EnqueueTransferStart(transport, read: true);
            EnqueueXFlashFrame(transport, bytes);
            EnqueueXFlashFrame(transport, new byte[4]);
            await session.ReadXFlashStorageAsync(8, 0, bytes.Length, destination,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(bytes, destination.ToArray());
        Assert.Null(session.DeviceInfo);
        Assert.True(session.IsConnected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachRunningAgentAsync_NoReply_WholeDeadlineFaultsSession(bool xml)
    {
        using var transport = new FakeCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            if (xml)
            {
                await session.AttachRunningXmlAgentAsync(50, TestContext.Current.CancellationToken);
            }
            else
            {
                await session.AttachRunningXFlashAgentAsync(MtkXFlashStorageKind.Emmc, 50, TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
        Assert.Null(session.XFlashStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachRunningAgentAsync_QueuedBootInitialization_CannotInterleave(bool xml)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        Task<MtkDeviceInfo>? queued = null;
        transport.OnWrite = _ =>
        {
            queued ??= session.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            return [];
        };
        if (xml)
        {
            EnqueueXmlStorage(transport, XmlHardwareDocument(false));
            await session.AttachRunningXmlAgentAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        else
        {
            EnqueueControlReply(transport, StorageGeometry(MtkXFlashStorageKind.Emmc));
            EnqueueControlReply(transport, PacketLengths());
            await session.AttachRunningXFlashAgentAsync(MtkXFlashStorageKind.Emmc,
                cancellationToken: TestContext.Current.CancellationToken);
        }
        var before = transport.Writes.Count;

        Assert.NotNull(queued);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await queued);
        Assert.Equal(before, transport.Writes.Count);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task AttachRunningXFlashAgentAsync_InvalidFamily_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.AttachRunningXFlashAgentAsync(
            (MtkXFlashStorageKind)(-1), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(MtkSessionState.Created, session.State);
    }

    [Theory]
    [InlineData(MtkXFlashStorageKind.Emmc, 0x040001u)]
    [InlineData(MtkXFlashStorageKind.Ufs, 0x040004u)]
    [InlineData(MtkXFlashStorageKind.Nor, 0x040003u)]
    [InlineData(MtkXFlashStorageKind.Nand, 0x040002u)]
    public async Task AttachRunningXFlashAgentAsync_IdleDa2_QueriesFreshGeometryWithoutBootOrUpload(
        MtkXFlashStorageKind kind, uint command)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        EnqueueControlReply(transport, kind switch
        {
            MtkXFlashStorageKind.Nor => NorGeometry(),
            MtkXFlashStorageKind.Nand => NandGeometry(),
            _ => StorageGeometry(kind)
        });
        EnqueueControlReply(transport, PacketLengths());
        transport.OnWrite = _ =>
        {
            Assert.Equal(MtkSessionState.Created, session.State);
            Assert.Null(session.XFlashStorage);
            return [];
        };

        var storage = await session.AttachRunningXFlashAgentAsync(kind, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(storage, session.XFlashStorage);
        Assert.Equal(kind, storage.Kind);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.Null(session.DeviceInfo);
        Assert.Null(session.XmlStorage);
        Assert.Equal(new uint[] { 0x010009, command, 0x010009, 0x040007 },
            transport.Writes.Where(data => data.Length == 4).Select(data => BinaryPrimitives.ReadUInt32LittleEndian(data)));
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachRunningXFlashAgentAsync(
            kind, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachRunningXmlAgentAsync_IdleDa2_PublishesOnlyAfterCompleteHandoff(bool ufs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        EnqueueXmlStorage(transport, XmlHardwareDocument(ufs));
        transport.OnWrite = _ =>
        {
            Assert.Equal(MtkSessionState.Created, session.State);
            Assert.Null(session.XmlStorage);
            return [];
        };

        var storage = await session.AttachRunningXmlAgentAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(storage, session.XmlStorage);
        Assert.Equal(ufs ? MtkXmlStorageKind.Ufs : MtkXmlStorageKind.Emmc, storage.Kind);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        Assert.Null(session.DeviceInfo);
        Assert.Null(session.XFlashStorage);
        Assert.Equal(new[] { "CMD:HOST-SUPPORTED-COMMANDS", "CMD:GET-HW-INFO" },
            transport.Writes.Where(data => data[0] == '<')
                .Select(data => XDocument.Parse(Encoding.UTF8.GetString(data).TrimEnd('\0')).Root!.Element("command")!.Value));
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachRunningXmlAgentAsync(
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("geometry")]
    [InlineData("packets")]
    [InlineData("rejected")]
    public async Task AttachRunningXFlashAgentAsync_InvalidReply_FaultsWithoutPublishingOrRetry(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        if (failure == "rejected")
        {
            EnqueueXFlashFrame(transport, new byte[] { 1, 0, 0, 0 });
        }
        else
        {
            EnqueueControlReply(transport, failure == "absent" ? new byte[4]
                : failure == "geometry" ? new byte[] { 1, 0, 0, 0 } : StorageGeometry(MtkXFlashStorageKind.Emmc));
            EnqueueControlReply(transport, failure == "packets" ? new byte[8] : PacketLengths());
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.AttachRunningXFlashAgentAsync(
            MtkXFlashStorageKind.Emmc, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
        Assert.Null(session.DeviceInfo);
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AttachRunningXmlAgentAsync(
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("unexpected-start")]
    [InlineData("bad-document")]
    public async Task AttachRunningXmlAgentAsync_InvalidReply_FaultsWithoutFallback(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        if (failure == "bad-document")
        {
            EnqueueXmlStorage(transport, new XElement("invalid"));
        }
        else
        {
            EnqueueXmlText(transport, failure == "rejected" ? "ERR!DENIED" : "<da><command>CMD:START</command></da>");
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.AttachRunningXmlAgentAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
        Assert.Null(session.DeviceInfo);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(false, 86400001)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(true, 86400001)]
    public async Task AttachRunningAgentAsync_InvalidDeadline_RejectsBeforeIo(bool xml, int timeoutMs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            if (xml)
            {
                await session.AttachRunningXmlAgentAsync(timeoutMs, TestContext.Current.CancellationToken);
            }
            else
            {
                await session.AttachRunningXFlashAgentAsync(MtkXFlashStorageKind.Emmc, timeoutMs, TestContext.Current.CancellationToken);
            }
        });

        Assert.Empty(transport.Writes);
        Assert.Equal(MtkSessionState.Created, session.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AttachRunningAgentAsync_Cancellation_PreservesOnlyUnstartedSession(bool xml, bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (xml)
            {
                await session.AttachRunningXmlAgentAsync(cancellationToken: cancellation.Token);
            }
            else
            {
                await session.AttachRunningXFlashAgentAsync(MtkXFlashStorageKind.Emmc, cancellationToken: cancellation.Token);
            }
        });

        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.Created, session.State);
        Assert.Null(session.XFlashStorage);
        Assert.Null(session.XmlStorage);
        Assert.Null(session.DeviceInfo);
        Assert.Equal(inFlight, transport.Writes.Count > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachRunningAgentAsync_BootSession_RejectsBeforeIo(bool xml)
    {
        using var transport = new ScriptedCommTransport();
        using var session = new MtkSession(transport);
        EnqueueIdentity(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (xml)
            {
                await session.AttachRunningXmlAgentAsync(cancellationToken: TestContext.Current.CancellationToken);
            }
            else
            {
                await session.AttachRunningXFlashAgentAsync(MtkXFlashStorageKind.Emmc, cancellationToken: TestContext.Current.CancellationToken);
            }
        });

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }
}