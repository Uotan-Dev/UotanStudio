namespace UotanStudio.Mirroring.AirPlay;

/// <summary>
/// Wire constants shared by the AirPlay receiver: service identity, feature bits, payload identifiers and
/// the bounds applied to every value read from the device.
/// <para>AirPlay 接收端共用的线协议常量：服务标识、特性位、负载标识，以及应用于每个设备侧读取值的上限。</para>
/// </summary>
internal static class AirPlayConstants
{
    /// <summary>TCP port the receiver advertises as its RTSP control endpoint.</summary>
    public const int DefaultRtspPort = 7100;

    /// <summary>mDNS service type the mirroring client browses for.</summary>
    public const string AirPlayServiceType = "_airplay._tcp.local";

    /// <summary>mDNS service type carrying the audio-only description of the same instance.</summary>
    public const string RaopServiceType = "_raop._tcp.local";

    /// <summary>Source version string announced in TXT records.</summary>
    public const string SourceVersion = "220.68";

    /// <summary>Mirroring feature bitmap announced in TXT records.</summary>
    public const ulong Features = 0x5A7FFFF7;

    /// <summary>Model identifier announced in TXT records.</summary>
    public const string Model = "UotanStudio,1";

    /// <summary>Protocol version announced in TXT records.</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>RTSP payload type of the H.264 mirroring channel.</summary>
    public const byte VideoPayloadType = 96;

    /// <summary>RTSP payload type of the AAC-ELD mirroring channel.</summary>
    public const byte AudioPayloadType = 103;

    /// <summary>Bytes of the mirror packet header that precede the H.264 NAL units.</summary>
    public const int MirrorHeaderLength = 128;

    /// <summary>Offset of the big-endian payload length inside the mirror packet header.</summary>
    public const int PayloadLengthOffset = 0;

    /// <summary>Offset of the 32.32 fixed-point timestamp inside the mirror packet header.</summary>
    public const int TimestampOffset = 6;

    /// <summary>Offset of the NAL unit count inside the mirror packet header.</summary>
    public const int NaluCountOffset = 47;

    /// <summary>Four-character code reported for the H.264 stream.</summary>
    public const uint H264FourCc = 0x68323634;

    /// <summary>Sample rate of the mirrored AAC-ELD audio stream.</summary>
    public const int AudioSampleRate = 44_100;

    /// <summary>Channel count of the mirrored AAC-ELD audio stream.</summary>
    public const int AudioChannels = 2;

    /// <summary>Samples carried by one mirrored AAC-ELD frame.</summary>
    public const int AudioFrameSamples = 480;

    /// <summary>Upper bound for one RTSP request or response header block.</summary>
    public const int MaximumHeaderBytes = 8 * 1024;

    /// <summary>Upper bound for the number of header lines in one RTSP message.</summary>
    public const int MaximumHeaderLines = 64;

    /// <summary>Upper bound for one RTSP plist body.</summary>
    public const int MaximumBodyBytes = 1024 * 1024;

    /// <summary>Upper bound for one H.264 access unit.</summary>
    public const int MaximumAccessUnitBytes = 32 * 1024 * 1024;

    /// <summary>Upper bound for one mirrored audio packet.</summary>
    public const int MaximumAudioPacketBytes = 4 * 1024 * 1024;

    /// <summary>Upper bound for the NAL units carried by one mirror packet.</summary>
    public const int MaximumNaluCount = 256;

    /// <summary>Upper bound for one RTP packet accepted from the device.</summary>
    public const int MaximumRtpPacketBytes = 64 * 1024 + MirrorHeaderLength;

    /// <summary>RTSP methods advertised in the OPTIONS response.</summary>
    public const string PublicMethods =
        "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET, SETUP";
}