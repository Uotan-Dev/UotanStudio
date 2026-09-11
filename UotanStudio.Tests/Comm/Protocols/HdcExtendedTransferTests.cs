using System.IO.Compression;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Hdc;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public sealed class HdcExtendedTransferTests
{
    [Fact]
    public void InstallDirectory_SendsOneTarTask_WithOnlyPackageFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "hdc-install-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "entry.hap"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "shared.hsp"), [4, 5]);
        File.WriteAllText(Path.Combine(root, "not-a-package.txt"), "excluded");
        try
        {
            long expected = -1;
            using var data = new MemoryStream();
            using var session = HdcTransferTests.Session(message =>
            {
                if (message.Command == HdcCommand.AppCheck)
                {
                    var config = HdcTransferCodec.DecodeConfig(message.Payload!);
                    Assert.EndsWith(".tar", config.OptionalName);
                    Assert.Equal("install", config.FunctionName);
                    expected = config.FileSize;
                    return [Reply(message, HdcCommand.AppBegin, [])];
                }
                if (message.Command == HdcCommand.AppData)
                {
                    data.Write(HdcTransferCodec.DecodeChunk(message.Payload!).Data);
                    if (data.Length == expected) return [Reply(message, HdcCommand.AppFinish, [1, 1, .. "OK"u8.ToArray()])];
                }
                return [];
            });
            Assert.Equal("OK", new HdcAppClient(session.Connection).InstallDirectory(root, "-r", TestContext.Current.CancellationToken));
            data.Position = 0;
            using var reader = new System.Formats.Tar.TarReader(data);
            var first = reader.GetNextEntry(); Assert.NotNull(first); Assert.Equal("entry.hap", first.Name);
            using var contents = new MemoryStream(); first.DataStream!.CopyTo(contents); Assert.Equal(new byte[] { 1, 2, 3 }, contents.ToArray());
            var second = reader.GetNextEntry(); Assert.NotNull(second); Assert.Equal("shared.hsp", second.Name);
            Assert.Null(reader.GetNextEntry());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DirectorySend_UsesOneTask_AndAcknowledgesEveryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "hdc-send-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllBytes(Path.Combine(root, "empty"), []);
        File.WriteAllBytes(Path.Combine(root, "nested", "space file"), [1, 2, 3]);
        var tasks = new HashSet<uint>();
        var completed = 0;
        var finished = false;
        TransferConfig? current = null;
        try
        {
            using var session = HdcTransferTests.Session(message =>
            {
                if (message.Command == HdcCommand.WakeupSlaveTask) tasks.Add(message.ChannelId);
                if (message.Command == HdcCommand.FileCheck)
                {
                    Assert.Null(current);
                    current = HdcTransferCodec.DecodeConfig(message.Payload!);
                    Assert.Equal("/remote", current.Path);
                    Assert.StartsWith(Path.GetFileName(root) + "/", current.OptionalName);
                    return [Reply(message, HdcCommand.FileBegin, [])];
                }
                if (message.Command == HdcCommand.FileData)
                {
                    Assert.NotNull(current);
                    var data = HdcTransferCodec.DecodeChunk(message.Payload!).Data;
                    Assert.Equal(current.FileSize, data.Length);
                    Assert.Equal(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(root)!, current.OptionalName)), data);
                    current = null; completed++;
                    return [Reply(message, HdcCommand.FileFinish, [1])];
                }
                if (message.Command == HdcCommand.FileFinish)
                {
                    Assert.Equal(2, completed);
                    Assert.Equal(new byte[] { 0 }, message.Payload);
                    finished = true;
                }
                return [];
            });
            using var transfer = new HdcSyncClient(session.Connection);
            transfer.SendDirectory(root, "/remote", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Single(tasks);
            Assert.True(finished);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DirectorySend_BudgetFailureDoesNotCreateRemoteTask()
    {
        var root = Path.Combine(Path.GetTempPath(), "hdc-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "file"), [1, 2]);
        try
        {
            var opened = false;
            using var session = HdcTransferTests.Session(_ => { opened = true; return []; });
            using var transfer = new HdcSyncClient(session.Connection);
            Assert.Throws<IOException>(() => transfer.SendDirectory(root, "/remote", maximumBytes: 1, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(opened);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AppContainer_SelectsMatchingModule_AndPreservesItsBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".app");
        var original = RandomNumberGenerator.GetBytes(100000);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("pack.info").Open()))
                writer.Write("""{"packages":[{"name":"phone","deviceType":["phone","tablet"],"deliveryWithInstall":true},{"name":"pc","deviceType":["2in1"],"deliveryWithInstall":true}]}""");
            using (var entry = archive.CreateEntry("phone.hap").Open()) entry.Write(original);
            using (var entry = archive.CreateEntry("pc.hap").Open()) entry.Write([9]);
        }
        try
        {
            using var received = new MemoryStream();
            using var session = HdcTransferTests.Session(message =>
            {
                if (message.Command == HdcCommand.AppCheck)
                {
                    var config = HdcTransferCodec.DecodeConfig(message.Payload!);
                    Assert.Equal(original.Length, config.FileSize);
                    Assert.Equal("-r", config.Options);
                    return [Reply(message, HdcCommand.AppBegin, [])];
                }
                if (message.Command == HdcCommand.AppData)
                {
                    received.Write(HdcTransferCodec.DecodeChunk(message.Payload!).Data);
                    if (received.Length == original.Length) return [Reply(message, HdcCommand.AppFinish, [1, 1, .. "OK"u8.ToArray()])];
                }
                return [];
            });
            Assert.Equal("OK", new HdcAppClient(session.Connection).InstallAppBundle(path, "tablet", "-r", TestContext.Current.CancellationToken));
            Assert.Equal(original, received.ToArray());
        }
        finally { File.Delete(path); }
    }

    private static byte[] Reply(HdcMessage message, HdcCommand command, byte[] payload)
        => HdcDaemonCodec.Encode(new(command, message.ChannelId, 0, payload));
}
