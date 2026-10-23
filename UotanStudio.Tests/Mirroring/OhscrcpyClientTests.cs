using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Mirroring;

public sealed class OhscrcpyClientTests
{
    private static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];

    [Fact]
    public void Connect_FirstAccessUnitCarriesSps_ExposesMetadataAndEmitsConfig()
    {
        var sps = OhscrcpyTestData.BuildSps(80, 45, 0);
        var accessUnit = OhscrcpyTestData.AnnexB(sps, Pps, [0x65, 0x88, 0x84]);
        var transport = new FakeVideoTransport(
            [OhscrcpyTestData.BuildGrpcFrame(OhscrcpyTestData.BuildVideoReply(accessUnit))]);
        using var client = new OhscrcpyClient(transport, null, new OhscrcpyClientOptions("serial"));

        client.Connect(2_000);

        Assert.NotNull(client.VideoMetadata);
        Assert.Equal(1280, client.VideoMetadata!.Width);
        Assert.Equal(720, client.VideoMetadata.Height);
        Assert.Equal(1, client.ConfigEpoch);
        Assert.NotNull(client.VideoConfig);

        using var config = client.ReadFrame(100);
        Assert.NotNull(config);
        Assert.True(config!.IsCodecConfig);
        Assert.Equal(OhscrcpyProtocol.BuildAnnexBConfiguration(sps, Pps), config.Data);

        using var frame = client.ReadFrame(100);
        Assert.NotNull(frame);
        Assert.False(frame!.IsCodecConfig);
        Assert.True(frame.IsKeyFrame);
        Assert.Equal(accessUnit, frame.Data);
    }

    [Fact]
    public void Connect_FirstAccessUnitWithoutSps_UsesControlDisplaySize()
    {
        var control = new FakeControl(displayWidth: 1320, displayHeight: 2848);
        var accessUnit = OhscrcpyTestData.AnnexB([0x41, 0x9A, 0x20]);
        var transport = new FakeVideoTransport(
            [OhscrcpyTestData.BuildGrpcFrame(OhscrcpyTestData.BuildVideoReply(accessUnit))]);
        using var client = new OhscrcpyClient(transport, control, new OhscrcpyClientOptions("serial"));

        client.Connect(2_000);

        Assert.Equal(1320, client.VideoMetadata!.Width);
        Assert.Equal(2848, client.VideoMetadata.Height);
        using var frame = client.ReadFrame(100);
        Assert.NotNull(frame);
        Assert.False(frame!.IsCodecConfig);
        Assert.False(frame.IsKeyFrame);
        Assert.Equal(accessUnit, frame.Data);
    }

    [Fact]
    public void Connect_ControlHandshakeFails_StillConnectsVideo()
    {
        var sps = OhscrcpyTestData.BuildSps(80, 45, 0);
        var accessUnit = OhscrcpyTestData.AnnexB(sps, Pps, [0x65, 0x88]);
        var transport = new FakeVideoTransport(
            [OhscrcpyTestData.BuildGrpcFrame(OhscrcpyTestData.BuildVideoReply(accessUnit))]);
        var control = new FakeControl(failConnect: true);
        using var client = new OhscrcpyClient(transport, control, new OhscrcpyClientOptions("serial"));

        client.Connect(2_000);

        Assert.NotNull(client.VideoMetadata);
        Assert.False(client.IsControlConnected);
    }

    [Fact]
    public void ReadFrame_StreamEndedWithoutFurtherPayload_ThrowsIOException()
    {
        var sps = OhscrcpyTestData.BuildSps(80, 45, 0);
        var accessUnit = OhscrcpyTestData.AnnexB(sps, Pps, [0x65, 0x88]);
        var transport = new FakeVideoTransport(
            [OhscrcpyTestData.BuildGrpcFrame(OhscrcpyTestData.BuildVideoReply(accessUnit))]);
        using var client = new OhscrcpyClient(transport, null, new OhscrcpyClientOptions("serial"));
        client.Connect(2_000);
        using var config = client.ReadFrame(100);
        using var frame = client.ReadFrame(100);

        Assert.NotNull(config);
        Assert.NotNull(frame);
        Assert.Throws<IOException>(() => client.ReadFrame(200));
    }

    [Fact]
    public void SendScrcpyControl_TouchAndScroll_DispatchesUiTestGestures()
    {
        using var client = CreateConnectedClient(out var control, out _);
        var touch = ScrcpyControlMessageWriter.InjectTouchEvent(
            ScrcpyConstants.ActionDown, 1, 100, 200, 1280, 720);
        var scroll = ScrcpyControlMessageWriter.InjectScrollEvent(100, 200, 1280, 720, 0, -1);

        Assert.True(client.SendScrcpyControl(touch));
        Assert.True(client.SendScrcpyControl(scroll));

        Assert.Equal(("touchDown", 100, 200), control.Gestures[0]);
        Assert.Equal(("AxisDown", 100, 200), control.Gestures[1]);
        Assert.Equal(("AxisStop", 100, 200), control.Gestures[2]);
    }

    [Fact]
    public void SendScrcpyControl_Clipboard_ReturnsFalse()
    {
        using var client = CreateConnectedClient(out var control, out _);

        Assert.False(client.SendScrcpyControl(ScrcpyControlMessageWriter.SetClipboard("text", paste: false)));
        Assert.Empty(control.Gestures);
        Assert.Empty(control.Keys);
        Assert.Empty(control.Texts);
    }

    [Fact]
    public void SendBackAndVolume_DispatchesOpenHarmonyKeyPresses()
    {
        using var client = CreateConnectedClient(out var control, out _);

        client.SendBack();
        client.SendVolumeKey(up: true);
        client.ToggleDisplayPower();

        Assert.Equal((OhscrcpyConstants.KeyCodeBack, OhscrcpyKeyAction.Press), control.Keys[0]);
        Assert.Equal((OhscrcpyConstants.KeyCodeVolumeUp, OhscrcpyKeyAction.Press), control.Keys[1]);
        Assert.Equal((OhscrcpyConstants.KeyCodePower, OhscrcpyKeyAction.Press), control.Keys[2]);
    }

    [Fact]
    public void SendScrcpyControl_AppSwitch_DispatchesOneRecentTasksChord()
    {
        using var client = CreateConnectedClient(out var control, out _);
        var down = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionDown, 187, 0, 0);
        var up = ScrcpyControlMessageWriter.InjectKeyCode(ScrcpyConstants.ActionUp, 187, 0, 0);

        Assert.True(client.SendScrcpyControl(down));
        Assert.False(client.SendScrcpyControl(up));

        Assert.Empty(control.Keys);
        Assert.Equal(
            (OhscrcpyConstants.KeyCodeMetaLeft, OhscrcpyConstants.KeyCodeTab), Assert.Single(control.Chords));
    }

    [Fact]
    public void Dispose_CalledTwice_ReleasesTransportAndControlOnce()
    {
        var client = CreateConnectedClient(out var control, out var transport);

        client.Dispose();
        client.Dispose();

        Assert.True(transport.Disposed);
        Assert.True(control.Disposed);
    }

    private static OhscrcpyClient CreateConnectedClient(out FakeControl control, out FakeVideoTransport transport)
    {
        var sps = OhscrcpyTestData.BuildSps(80, 45, 0);
        var accessUnit = OhscrcpyTestData.AnnexB(sps, Pps, [0x65, 0x88]);
        control = new FakeControl();
        transport = new FakeVideoTransport(
            [OhscrcpyTestData.BuildGrpcFrame(OhscrcpyTestData.BuildVideoReply(accessUnit))]);
        var client = new OhscrcpyClient(transport, control, new OhscrcpyClientOptions("serial"));
        client.Connect(2_000);
        return client;
    }

    private sealed class FakeVideoTransport : IOhoscrcpyVideoTransport
    {
        private readonly MemoryStream _stream;

        internal FakeVideoTransport(IEnumerable<byte[]> frames)
        {
            using var buffer = new MemoryStream();
            foreach (var frame in frames) buffer.Write(frame);
            _stream = new MemoryStream(buffer.ToArray());
        }

        internal int KeyFrameRequests { get; private set; }

        internal bool Disposed { get; private set; }

        public Stream OpenVideoStream(CancellationToken cancellationToken) => _stream;

        public void RequestKeyFrame(CancellationToken cancellationToken) => KeyFrameRequests++;

        public void Dispose()
        {
            Disposed = true;
            _stream.Dispose();
        }
    }

    private sealed class FakeControl(
        bool failConnect = false, int displayWidth = 0, int displayHeight = 0) : IOhscrcpyControlChannel
    {
        internal List<(string Api, int X, int Y)> Gestures { get; } = [];

        internal List<(int Code, OhscrcpyKeyAction Action)> Keys { get; } = [];

        internal List<(int Modifier, int Code)> Chords { get; } = [];

        internal List<string> Texts { get; } = [];

        internal bool Disposed { get; private set; }

        public bool IsConnected { get; private set; }

        public int DisplayWidth => displayWidth;

        public int DisplayHeight => displayHeight;

        public void Connect()
        {
            if (failConnect) throw new IOException("control unavailable");
            IsConnected = true;
        }

        public void SendGesture(string api, int x, int y) => Gestures.Add((api, x, y));

        public void SendScroll(bool up, int x, int y)
        {
            Gestures.Add((up ? "AxisUp" : "AxisDown", x, y));
            Gestures.Add(("AxisStop", x, y));
        }

        public void SendKey(int keyCode, OhscrcpyKeyAction action) => Keys.Add((keyCode, action));

        public void SendKeyChord(int modifierKeyCode, int keyCode) => Chords.Add((modifierKeyCode, keyCode));

        public void SendText(string text) => Texts.Add(text);

        public void Dispose()
        {
            Disposed = true;
            IsConnected = false;
        }
    }
}