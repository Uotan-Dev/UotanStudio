using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;
using Xunit;

namespace UotanStudio.Tests.Devices;

public sealed class HdcServerTransferTests
{
    private const string ConnectKey = "usb-key";

    [Fact(Timeout = 30000)]
    public async Task Send_RegularFile_ExchangesCheckDataAndCompletionFrames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "payload.bin");
            var content = new byte[70000];
            Random.Shared.NextBytes(content);
            await File.WriteAllBytesAsync(source, content, cancellationToken);
            const string remotePath = "/data/local/tmp/payload.bin";
            await using var server = ScriptedServer.Start(async (stream, connectKey) =>
            {
                Assert.Equal(ConnectKey, connectKey);
                var command = await ReadTextCommandAsync(stream, cancellationToken);
                Assert.StartsWith("file send ", command, StringComparison.Ordinal);
                Assert.Contains(source, command, StringComparison.Ordinal);
                Assert.Contains(" " + remotePath + " remote -cwd \"", command, StringComparison.Ordinal);
                await WriteTaskFrameAsync(stream, HdcCommand.FileInit,
                    Encoding.UTF8.GetBytes(source + " " + remotePath), cancellationToken);
                await AssertIdleSlaveTaskAsync(stream, cancellationToken);
                var (checkCommand, checkPayload) = await ReadTaskFrameAsync(stream, cancellationToken);
                Assert.Equal(HdcCommand.FileCheck, checkCommand);
                var config = HdcTransferCodec.DecodeConfig(checkPayload);
                Assert.Equal(content.Length, checked((int)config.FileSize));
                Assert.Equal(remotePath, config.Path);
                Assert.Equal("payload.bin", config.OptionalName);
                await WriteTaskFrameAsync(stream, HdcCommand.FileBegin, ReadOnlyMemory<byte>.Empty, cancellationToken);
                long received = 0;
                while (received < content.Length)
                {
                    var (dataCommand, dataPayload) = await ReadTaskFrameAsync(stream, cancellationToken);
                    Assert.Equal(HdcCommand.FileData, dataCommand);
                    var chunk = HdcTransferCodec.DecodeChunk(dataPayload);
                    Assert.Equal(received, chunk.Offset);
                    Assert.Equal(content.AsSpan(checked((int)received), chunk.Data.Length).ToArray(), chunk.Data);
                    received += chunk.Data.Length;
                }

                await WriteTaskFrameAsync(stream, HdcCommand.FileFinish, new byte[] { 1 }, cancellationToken);
                var (finishCommand, finishPayload) = await ReadTaskFrameAsync(stream, cancellationToken);
                Assert.Equal(HdcCommand.FileFinish, finishCommand);
                Assert.Equal(new byte[] { 0 }, finishPayload);
            }, cancellationToken);
            using var client = new HdcServerSyncClient(new HdcServerClient("127.0.0.1", server.Port), ConnectKey);
            await Task.Run(() => client.Send(source, remotePath, cancellationToken: cancellationToken), cancellationToken);
            await server.Completion;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Recv_RegularFile_PublishesOnlyAfterCompletion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryDirectory();
        try
        {
            const string remotePath = "/data/local/tmp/capture.bin";
            var content = Encoding.ASCII.GetBytes("uotan hdc server transfer");
            var destination = Path.Combine(root, "capture.bin");
            await using var server = ScriptedServer.Start(async (stream, connectKey) =>
            {
                Assert.Equal(ConnectKey, connectKey);
                var command = await ReadTextCommandAsync(stream, cancellationToken);
                Assert.StartsWith("file recv ", command, StringComparison.Ordinal);
                Assert.Contains(remotePath, command, StringComparison.Ordinal);
                Assert.Contains(" remote -cwd \"", command, StringComparison.Ordinal);
                var config = new TransferConfig { FileSize = content.Length, Path = destination, OptionalName = "capture.bin" };
                await WriteTaskFrameAsync(stream, HdcCommand.FileCheck, HdcTransferCodec.EncodeConfig(config), cancellationToken);
                var (beginCommand, _) = await ReadTaskFrameAsync(stream, cancellationToken);
                Assert.Equal(HdcCommand.FileBegin, beginCommand);
                await WriteTaskFrameAsync(stream, HdcCommand.FileData, HdcTransferCodec.EncodeChunk(0, content), cancellationToken);
                var (finishCommand, finishPayload) = await ReadTaskFrameAsync(stream, cancellationToken);
                Assert.Equal(HdcCommand.FileFinish, finishCommand);
                Assert.Equal(new byte[] { 1 }, finishPayload);
                await WriteTaskFrameAsync(stream, HdcCommand.FileFinish, new byte[] { 0 }, cancellationToken);
            }, cancellationToken);
            using var client = new HdcServerSyncClient(new HdcServerClient("127.0.0.1", server.Port), ConnectKey);
            await Task.Run(() => client.Recv(remotePath, destination, cancellationToken), cancellationToken);
            await server.Completion;
            Assert.Equal(content, await File.ReadAllBytesAsync(destination, cancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task RecvDirectory_TwoEntries_CreatesNestedTree()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryDirectory();
        try
        {
            const string remotePath = "/data/local/tmp/tree";
            var first = Encoding.ASCII.GetBytes("first entry");
            var second = Encoding.ASCII.GetBytes("second entry");
            var destination = Path.Combine(root, "tree");
            await using var server = ScriptedServer.Start(async (stream, connectKey) =>
            {
                var command = await ReadTextCommandAsync(stream, cancellationToken);
                Assert.StartsWith("file recv ", command, StringComparison.Ordinal);
                Assert.Contains(" remote -cwd \"", command, StringComparison.Ordinal);
                await WriteDirectoryEntryAsync(stream, destination, "tree/one.txt", first, cancellationToken);
                await WriteDirectoryEntryAsync(stream, destination, "tree/sub/two.txt", second, cancellationToken);
                await WriteTaskFrameAsync(stream, HdcCommand.FileFinish, new byte[] { 0 }, cancellationToken);
            }, cancellationToken);
            using var client = new HdcServerSyncClient(new HdcServerClient("127.0.0.1", server.Port), ConnectKey);
            await Task.Run(() => client.RecvDirectory(remotePath, destination, 1 << 20, cancellationToken: cancellationToken),
                cancellationToken);
            await server.Completion;
            Assert.Equal(first, await File.ReadAllBytesAsync(
                Path.Combine(destination, "tree", "one.txt"), cancellationToken));
            Assert.Equal(second, await File.ReadAllBytesAsync(
                Path.Combine(destination, "tree", "sub", "two.txt"), cancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Send_ErrorTextFrame_IsRejectedWithServerMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "payload.bin");
            await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3, 4 }, cancellationToken);
            await using var server = ScriptedServer.Start(async (stream, connectKey) =>
            {
                _ = await ReadTextCommandAsync(stream, cancellationToken);
                await WriteTextFrameAsync(stream, "[Fail]no matching target\r\n", cancellationToken);
            }, cancellationToken);
            using var client = new HdcServerSyncClient(new HdcServerClient("127.0.0.1", server.Port), ConnectKey);
            var error = await Assert.ThrowsAsync<IOException>(() => Task.Run(
                () => client.Send(source, "/data/local/tmp/payload.bin", cancellationToken: cancellationToken),
                cancellationToken));
            await server.Completion;
            Assert.Contains("[Fail]no matching target", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Install_HapPackage_StreamsAppCheckAndReturnsResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryDirectory();
        try
        {
            var package = Path.Combine(root, "entry.hap");
            var content = Encoding.ASCII.GetBytes("hap payload");
            await File.WriteAllBytesAsync(package, content, cancellationToken);
            await using var server = ScriptedServer.Start(async (stream, connectKey) =>
            {
                var command = await ReadTextCommandAsync(stream, cancellationToken);
                Assert.StartsWith("install ", command, StringComparison.Ordinal);
                Assert.Contains(package, command, StringComparison.Ordinal);
                Assert.Contains(" remote -cwd \"", command, StringComparison.Ordinal);
                await WriteTaskFrameAsync(stream, HdcCommand.AppInit, Encoding.UTF8.GetBytes(package), cancellationToken);
                await AssertIdleSlaveTaskAsync(stream, cancellationToken);
                var (checkCommand, checkPayload) = await ReadTaskFrameAsync(stream, cancellationToken);
                Assert.Equal(HdcCommand.AppCheck, checkCommand);
                var config = HdcTransferCodec.DecodeConfig(checkPayload);
                Assert.Equal(content.Length, checked((int)config.FileSize));
                Assert.Equal("install", config.FunctionName);
                Assert.EndsWith(".hap", config.OptionalName, StringComparison.Ordinal);
                await WriteTaskFrameAsync(stream, HdcCommand.AppBegin, ReadOnlyMemory<byte>.Empty, cancellationToken);
                long received = 0;
                while (received < content.Length)
                {
                    var (dataCommand, dataPayload) = await ReadTaskFrameAsync(stream, cancellationToken);
                    Assert.Equal(HdcCommand.AppData, dataCommand);
                    var chunk = HdcTransferCodec.DecodeChunk(dataPayload);
                    received += chunk.Data.Length;
                }

                Assert.Equal((long)content.Length, received);
                var result = new byte[2 + Encoding.UTF8.GetByteCount("install bundle successfully.")];
                result[0] = 1;
                result[1] = 1;
                Encoding.UTF8.GetBytes("install bundle successfully.", result.AsSpan(2));
                await WriteTaskFrameAsync(stream, HdcCommand.AppFinish, result, cancellationToken);
            }, cancellationToken);
            var client = new HdcServerAppClient(new HdcServerClient("127.0.0.1", server.Port), ConnectKey);
            var text = await Task.Run(() => client.Install(package, cancellationToken), cancellationToken);
            await server.Completion;
            Assert.Equal("install bundle successfully.", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Uninstall_TextResult_ReturnsSuccessText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = ScriptedServer.Start(async (stream, connectKey) =>
        {
            var command = await ReadTextCommandAsync(stream, cancellationToken);
            Assert.Equal("uninstall com.example.app", command);
            await WriteTextFrameAsync(stream, "[Info]App uninstall path: msg:uninstall bundle successfully.\r\n", cancellationToken);
        }, cancellationToken);
        var client = new HdcServerAppClient(new HdcServerClient("127.0.0.1", server.Port), ConnectKey);
        var text = await Task.Run(() => client.Uninstall("com.example.app", cancellationToken), cancellationToken);
        await server.Completion;
        Assert.Contains("uninstall bundle successfully.", text, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30000)]
    public async Task Uninstall_AppResultFrame_IsAccepted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = ScriptedServer.Start(async (stream, connectKey) =>
        {
            var command = await ReadTextCommandAsync(stream, cancellationToken);
            Assert.Equal("uninstall -k com.example.app", command);
            var result = new byte[2 + Encoding.UTF8.GetByteCount("uninstall bundle successfully.")];
            result[0] = 2;
            result[1] = 1;
            Encoding.UTF8.GetBytes("uninstall bundle successfully.", result.AsSpan(2));
            await WriteTaskFrameAsync(stream, HdcCommand.AppFinish, result, cancellationToken);
        }, cancellationToken);
        var client = new HdcServerAppClient(new HdcServerClient("127.0.0.1", server.Port), ConnectKey);
        var text = await Task.Run(() => client.Uninstall("com.example.app", "-k", cancellationToken), cancellationToken);
        await server.Completion;
        Assert.Equal("uninstall bundle successfully.", text);
    }

    [Fact(Timeout = 30000)]
    public async Task InstallAppBundle_MatchingDeviceType_InstallsSelectedModule()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryDirectory();
        try
        {
            var module = Encoding.ASCII.GetBytes("signed module payload");
            var bundle = Path.Combine(root, "bundle.app");
            CreateAppBundle(bundle,
                """{"packages":[{"deviceType":["phone"],"name":"entry","deliveryWithInstall":true}]}""",
                ("entry.hap", module));
            await using var server = ScriptedServer.Start(async (stream, connectKey) =>
            {
                _ = await ReadTextCommandAsync(stream, cancellationToken);
                await WriteTaskFrameAsync(stream, HdcCommand.AppInit, ReadOnlyMemory<byte>.Empty, cancellationToken);
                await AssertIdleSlaveTaskAsync(stream, cancellationToken);
                var (checkCommand, checkPayload) = await ReadTaskFrameAsync(stream, cancellationToken);
                Assert.Equal(HdcCommand.AppCheck, checkCommand);
                var config = HdcTransferCodec.DecodeConfig(checkPayload);
                Assert.Equal(module.Length, checked((int)config.FileSize));
                Assert.EndsWith(".hap", config.OptionalName, StringComparison.Ordinal);
                await WriteTaskFrameAsync(stream, HdcCommand.AppBegin, ReadOnlyMemory<byte>.Empty, cancellationToken);
                long received = 0;
                while (received < module.Length)
                {
                    var (_, dataPayload) = await ReadTaskFrameAsync(stream, cancellationToken);
                    var chunk = HdcTransferCodec.DecodeChunk(dataPayload);
                    Assert.Equal(module.AsSpan(checked((int)received), chunk.Data.Length).ToArray(), chunk.Data);
                    received += chunk.Data.Length;
                }

                var result = new byte[2 + Encoding.UTF8.GetByteCount("install bundle successfully.")];
                result[0] = 1;
                result[1] = 1;
                Encoding.UTF8.GetBytes("install bundle successfully.", result.AsSpan(2));
                await WriteTaskFrameAsync(stream, HdcCommand.AppFinish, result, cancellationToken);
            }, cancellationToken);
            var client = new HdcServerAppClient(new HdcServerClient("127.0.0.1", server.Port), ConnectKey);
            var text = await Task.Run(() => client.InstallAppBundle(bundle, "phone", cancellationToken: cancellationToken),
                cancellationToken);
            await server.Completion;
            Assert.Equal("install bundle successfully.", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InstallAppBundle_NoMatchingDeviceType_IsRejectedBeforeConnecting()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var bundle = Path.Combine(root, "bundle.app");
            CreateAppBundle(bundle,
                """{"packages":[{"deviceType":["phone"],"name":"entry","deliveryWithInstall":true}]}""",
                ("entry.hap", new byte[] { 1, 2, 3 }));
            var client = new HdcServerAppClient(new HdcServerClient("127.0.0.1", 1), ConnectKey);
            Assert.Throws<NotSupportedException>(() => client.InstallAppBundle(
                bundle, "tablet", cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ServerHdcSession_CreatesServerRoutedTransferClients()
    {
        using var server = new HdcServerClient("127.0.0.1", 1);
        using var session = server.CreateSession(ConnectKey);
        using var sync = session.CreateSyncClient();
        var app = session.CreateAppClient();
        Assert.IsType<HdcServerSyncClient>(sync);
        Assert.IsType<HdcServerAppClient>(app);
        Assert.Throws<NotSupportedException>(() => _ = session.Forwards);
    }

    private static async Task AssertIdleSlaveTaskAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var (command, payload) = await ReadTaskFrameAsync(stream, cancellationToken);
        Assert.Equal(HdcCommand.WakeupSlaveTask, command);
        Assert.Empty(payload);
    }

    private static async Task WriteDirectoryEntryAsync(NetworkStream stream, string destination, string optionalName,
        byte[] content, CancellationToken cancellationToken)
    {
        var config = new TransferConfig { FileSize = content.Length, Path = destination, OptionalName = optionalName };
        await WriteTaskFrameAsync(stream, HdcCommand.FileCheck, HdcTransferCodec.EncodeConfig(config), cancellationToken);
        var (beginCommand, _) = await ReadTaskFrameAsync(stream, cancellationToken);
        Assert.Equal(HdcCommand.FileBegin, beginCommand);
        await WriteTaskFrameAsync(stream, HdcCommand.FileData, HdcTransferCodec.EncodeChunk(0, content), cancellationToken);
        var (finishCommand, finishPayload) = await ReadTaskFrameAsync(stream, cancellationToken);
        Assert.Equal(HdcCommand.FileFinish, finishCommand);
        Assert.Equal(new byte[] { 1 }, finishPayload);
    }

    private static void CreateAppBundle(string path, string packInfo, params (string Name, byte[] Content)[] modules)
    {
        using var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        using (var metadata = archive.CreateEntry("pack.info").Open())
        {
            metadata.Write(Encoding.UTF8.GetBytes(packInfo));
        }

        foreach (var (name, content) in modules)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(content);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "uotan-hdc-server-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WriteTextFrameAsync(NetworkStream stream, string text, CancellationToken cancellationToken)
        => await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(text), cancellationToken);

    private static async Task WriteTaskFrameAsync(NetworkStream stream, HdcCommand command,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)command);
        payload.Span.CopyTo(frame.AsSpan(2));
        await WriteFrameAsync(stream, frame, cancellationToken);
    }

    private static async Task WriteFrameAsync(NetworkStream stream, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<(HdcCommand Command, byte[] Payload)> ReadTaskFrameAsync(NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync(stream, cancellationToken);
        Assert.True(frame.Length >= 2);
        return ((HdcCommand)BinaryPrimitives.ReadUInt16LittleEndian(frame), frame[2..]);
    }

    private static async Task<string> ReadTextCommandAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var frame = await ReadFrameAsync(stream, cancellationToken);
        var text = Encoding.UTF8.GetString(frame);
        Assert.EndsWith("\0", text, StringComparison.Ordinal);
        return text[..^1];
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        Assert.InRange(length, 1, HdcServerClient.MaximumFrameBytes);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task<string> GreetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var greeting = new byte[44];
        Encoding.ASCII.GetBytes("OHOS HDC", greeting);
        await WriteFrameAsync(stream, greeting, cancellationToken);
        var reply = await ReadFrameAsync(stream, cancellationToken);
        Assert.Equal(44, reply.Length);
        return Encoding.UTF8.GetString(reply, 12, 32).TrimEnd('\0');
    }

    private sealed class ScriptedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime;

        private ScriptedServer(TcpListener listener, Func<NetworkStream, string, Task> script,
            CancellationToken cancellationToken)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Completion = RunAsync(script);
        }

        public int Port { get; }

        public Task Completion { get; }

        public static ScriptedServer Start(Func<NetworkStream, string, Task> script, CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new ScriptedServer(listener, script, cancellationToken);
        }

        private async Task RunAsync(Func<NetworkStream, string, Task> script)
        {
            using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
            var stream = client.GetStream();
            var connectKey = await GreetAsync(stream, _lifetime.Token);
            await script(stream, connectKey);
            await stream.FlushAsync(_lifetime.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await Completion;
            }
            catch (Exception)
            {
            }

            _lifetime.Dispose();
        }
    }
}