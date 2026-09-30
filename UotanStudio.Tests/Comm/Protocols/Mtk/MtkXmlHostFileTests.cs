using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData("OK")]
    [InlineData("OK!EOT")]
    public async Task InitializeXmlHardwareAsync_DeviceUpload_ProvidesDeclaredLengthAndCompletes(string status)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        var store = new RecordingXmlHostFileStore();
        session.HostFileStore = store;
        EnqueueHostUpload(transport, "OK@0x3", [1, 2, 3], status);
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);

        await session.InitializeXmlHardwareAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, store.DeclaredLength);
        Assert.Equal("upload-token", Assert.Single(store.Tokens));
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Content.ToArray());
        Assert.Equal(1, store.Opens);
        Assert.False(store.Content.CanWrite);
        Assert.Equal(MtkSessionState.XmlHardwareInitialized, session.State);
    }

    [Theory]
    [InlineData("ERR!UPLOAD_FAILED")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task InitializeXmlHardwareAsync_DeviceUploadFinalError_FaultsWithoutSuccess(string status)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        var store = new RecordingXmlHostFileStore();
        session.HostFileStore = store;
        EnqueueHostUpload(transport, "OK@0x3", [1, 2, 3], status);
        EnqueueXmlCompletion(transport);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(3, store.DeclaredLength);
        Assert.False(store.Content.CanWrite);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("OK@0x0")]
    [InlineData("OK@0x4000001")]
    [InlineData("OK@0x7fffffffffffffff")]
    [InlineData("OK@0xffffffffffffffff")]
    [InlineData("ERR!SIZE")]
    public async Task InitializeXmlHardwareAsync_InvalidUploadLength_DoesNotOpenSink(string length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        var store = new RecordingXmlHostFileStore();
        session.HostFileStore = store;
        EnqueueHostUpload(transport, length, [], "OK");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, store.Opens);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task InitializeXmlHardwareAsync_ExplicitUploadLimit_RejectsBeforeOpeningSink()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        var store = new RecordingXmlHostFileStore();
        session.HostFileStore = store;
        session.MaximumHostFileTransferBytes = 2;
        EnqueueHostUpload(transport, "OK@0x3", [], "OK");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, store.Opens);
    }

    [Fact]
    public async Task InitializeXmlHardwareAsync_PrematureUploadEot_FaultsAndDisposesSink()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        var store = new RecordingXmlHostFileStore();
        session.HostFileStore = store;
        EnqueueHostUpload(transport, "OK@0x3", [1, 2], "OK!EOT");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new byte[] { 1, 2 }, store.Content.ToArray());
        Assert.False(store.Content.CanWrite);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("stream:secret")]
    [InlineData("dir\\file")]
    public async Task InitializeXmlHardwareAsync_DeviceUploadTargetEscapesRelativeProfile_RejectsWithoutOpeningSink(
        string target)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        var store = new RecordingXmlHostFileStore();
        session.HostFileStore = store;
        EnqueueHostUploadRequest(transport, target, "0x3");
        EnqueueXmlText(transport, "OK@0x3");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, store.Opens);
        Assert.Empty(store.Tokens);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("stream:secret")]
    [InlineData("dir\\file")]
    public async Task InitializeXmlHardwareAsync_DeviceFileSizePathEscapesRelativeProfile_RejectsWithoutQueryingStore(
        string path)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        var store = new RecordingXmlHostFileStore();
        session.HostFileStore = store;
        EnqueueXmlText(transport, "<host><version>1.0</version><command>CMD:FILE-SYS-OPERATION</command><arg>"
            + $"<key>FILE-SIZE</key><file_path>{path}</file_path></arg></host>");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, store.SizeQueries);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task InitializeXmlHardwareAsync_DeviceUploadNegotiatedPacketCountAboveBound_RejectsWithoutOpeningSink()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        var store = new RecordingXmlHostFileStore();
        session.HostFileStore = store;
        EnqueueHostUploadRequest(transport, "upload-token", "0x1");
        EnqueueXmlText(transport, "OK@0x10001");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, store.Opens);
        Assert.Empty(store.Tokens);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("stream:secret")]
    [InlineData("dir\\file")]
    public async Task InitializeXmlHardwareAsync_DeviceDownloadSourceEscapesRelativeProfile_RejectsWithoutReadingStore(
        string source)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlRuntimeSessionAsync(transport);
        var store = new RecordingXmlHostFileStore();
        session.HostFileStore = store;
        EnqueueXmlText(transport, "<host><version>1.0</version><command>CMD:DOWNLOAD-FILE</command><arg>"
            + "<checksum>CHK_NO</checksum><info>test</info>"
            + $"<source_file>{source}</source_file><packet_length>0x3</packet_length></arg></host>");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.InitializeXmlHardwareAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, store.ReadAttempts);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("token\u0001name")]
    [InlineData("token\u0000")]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("stream:secret")]
    [InlineData("dir\\file")]
    public void ValidateXmlHostFileToken_OutsideBoundedRelativeName_Throws(string token)
    {
        Assert.Throws<MtkProtocolException>(() => MtkSession.ValidateXmlHostFileToken(token));
    }

    [Theory]
    [InlineData("upload-token")]
    [InlineData("sub/dir/token.dat")]
    [InlineData("new.dat.br")]
    public void ValidateXmlHostFileToken_BoundedRelativeName_Accepts(string token)
    {
        MtkSession.ValidateXmlHostFileToken(token);
    }

    [Fact]
    public void ValidateXmlHostFileToken_TokenAtLengthCeiling_Accepts()
    {
        MtkSession.ValidateXmlHostFileToken(new string('x', 512));
    }

    [Fact]
    public void ValidateXmlHostFileToken_TokenAboveLengthCeiling_Throws()
    {
        Assert.Throws<MtkProtocolException>(() => MtkSession.ValidateXmlHostFileToken(new string('x', 513)));
    }

    private static void EnqueueHostUploadRequest(ScriptedCommTransport transport, string target, string packetLength)
    {
        EnqueueXmlText(transport, "<host><version>1.0</version><command>CMD:UPLOAD-FILE</command><arg>"
            + "<checksum>CHK_NO</checksum><info>test</info>"
            + $"<target_file>{target}</target_file>"
            + $"<packet_length>{packetLength}</packet_length></arg></host>");
    }

    private static void EnqueueHostUpload(ScriptedCommTransport transport, string length, byte[] data, string status)
    {
        EnqueueHostUploadRequest(transport, "upload-token", "0x3");
        EnqueueXmlText(transport, length);
        EnqueueXmlText(transport, "OK");
        if (data.Length > 0)
        {
            EnqueueXFlashFrame(transport, data);
        }
        EnqueueXmlText(transport, status);
    }

    private sealed class RecordingXmlHostFileStore : IMtkXmlHostFileStore
    {
        public MemoryStream Content { get; } = new();
        public long DeclaredLength { get; private set; }
        public int Opens { get; private set; }
        public int ReadAttempts { get; private set; }
        public int SizeQueries { get; private set; }
        public List<string> Tokens { get; } = [];

        public Task<(Stream Content, long Length)?> OpenReadAsync(string token, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tokens.Add(token);
            ReadAttempts++;
            return Task.FromResult<(Stream Content, long Length)?>(null);
        }

        public Task<Stream?> OpenWriteAsync(string token, long length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tokens.Add(token);
            DeclaredLength = length;
            Opens++;
            return Task.FromResult<Stream?>(Content);
        }

        public Task<long?> GetSizeAsync(string token, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Tokens.Add(token);
            SizeQueries++;
            return Task.FromResult<long?>(null);
        }
    }
}