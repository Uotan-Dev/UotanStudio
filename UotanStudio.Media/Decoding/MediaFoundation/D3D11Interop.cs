using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// Native interop for Direct3D 11 / DXGI used by the hardware decode path.
/// <para>硬件解码路径使用的 Direct3D 11 / DXGI 原生互操作。</para>
/// </summary>
/// <remarks>
/// <para>
/// Struct layouts and vtable slots follow d3d11.h / dxgi.h (Windows SDK 10.0.26100).
/// All native calls run in NativeAOT without COM marshalling.
/// </para>
/// <para>
/// 结构体布局与 vtable 槽位遵循 d3d11.h / dxgi.h（Windows SDK 10.0.26100）。
/// 全部原生调用在 NativeAOT 下运行，不使用 COM 编组。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class D3D11Interop
{
    private const string D3D11 = "d3d11.dll";

    /// <summary>IID_ID3D11VideoDevice.</summary>
    /// <summary>IID_ID3D11VideoDevice。</summary>
    public static readonly Guid IidVideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");

    /// <summary>IID_ID3D11VideoContext.</summary>
    /// <summary>IID_ID3D11VideoContext。</summary>
    public static readonly Guid IidVideoContext = new("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");

    /// <summary>IID_ID3D11Texture2D.</summary>
    /// <summary>IID_ID3D11Texture2D。</summary>
    public static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>IID_ID3D11Multithread (d3d11_4.h).</summary>
    /// <summary>IID_ID3D11Multithread（d3d11_4.h）。</summary>
    public static readonly Guid IidMultithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    /// <summary>IID_IMFDXGIBuffer.</summary>
    /// <summary>IID_IMFDXGIBuffer。</summary>
    public static readonly Guid IidDxgiBuffer = new("E7174CFA-1C9E-48B1-8866-626226BFC258");

    /// <summary>D3D11_CREATE_DEVICE_VIDEO_SUPPORT.</summary>
    /// <summary>D3D11_CREATE_DEVICE_VIDEO_SUPPORT。</summary>
    public const uint CreateDeviceVideoSupport = 0x800;

    /// <summary>D3D11_CREATE_DEVICE_BGRA_SUPPORT.</summary>
    /// <summary>D3D11_CREATE_DEVICE_BGRA_SUPPORT。</summary>
    public const uint CreateDeviceBgraSupport = 0x20;

    /// <summary>D3D_DRIVER_TYPE_HARDWARE (d3dcommon.h: UNKNOWN + 1).</summary>
    /// <summary>D3D_DRIVER_TYPE_HARDWARE（d3dcommon.h：UNKNOWN + 1）。</summary>
    public const uint DriverTypeHardware = 1;

    /// <summary>D3D11_SDK_VERSION.</summary>
    /// <summary>D3D11_SDK_VERSION。</summary>
    public const uint SdkVersion = 7;

    /// <summary>D3D11_USAGE_DEFAULT.</summary>
    /// <summary>D3D11_USAGE_DEFAULT。</summary>
    public const uint UsageDefault = 0;

    /// <summary>D3D11_USAGE_STAGING.</summary>
    /// <summary>D3D11_USAGE_STAGING。</summary>
    public const uint UsageStaging = 3;

    /// <summary>D3D11_BIND_DECODER.</summary>
    /// <summary>D3D11_BIND_DECODER。</summary>
    public const uint BindDecoder = 0x200;

    /// <summary>D3D11_BIND_RENDER_TARGET.</summary>
    /// <summary>D3D11_BIND_RENDER_TARGET。</summary>
    public const uint BindRenderTarget = 0x20;

    /// <summary>D3D11_BIND_SHADER_RESOURCE.</summary>
    /// <summary>D3D11_BIND_SHADER_RESOURCE。</summary>
    public const uint BindShaderResource = 0x8;

    /// <summary>D3D11_CPU_ACCESS_READ.</summary>
    /// <summary>D3D11_CPU_ACCESS_READ。</summary>
    public const uint CpuAccessRead = 0x20000;

    /// <summary>D3D11_CPU_ACCESS_WRITE.</summary>
    /// <summary>D3D11_CPU_ACCESS_WRITE。</summary>
    public const uint CpuAccessWrite = 0x10000;

    /// <summary>D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE (staging combination).</summary>
    /// <summary>D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE（staging 组合）。</summary>
    public const uint CpuAccessReadWrite = 0x30000;

    /// <summary>D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE.</summary>
    /// <summary>D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE。</summary>
    public const uint VideoFrameFormatProgressive = 0;

    /// <summary>D3D11_VPIV_DIMENSION_TEXTURE2D.</summary>
    /// <summary>D3D11_VPIV_DIMENSION_TEXTURE2D。</summary>
    public const uint VpivDimensionTexture2D = 1;

    /// <summary>D3D11_VPOV_DIMENSION_TEXTURE2D.</summary>
    /// <summary>D3D11_VPOV_DIMENSION_TEXTURE2D。</summary>
    public const uint VpovDimensionTexture2D = 1;

    /// <summary>DXGI_FORMAT_NV12.</summary>
    /// <summary>DXGI_FORMAT_NV12。</summary>
    public const uint DxgiFormatNv12 = 103;

    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM.</summary>
    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM。</summary>
    public const uint DxgiFormatB8G8R8A8 = 87;

    /// <summary>DXGI_FORMAT_R8_UNORM.</summary>
    /// <summary>DXGI_FORMAT_R8_UNORM。</summary>
    public const uint DxgiFormatR8 = 61;

    /// <summary>MF_E_NO_EVENTS_AVAILABLE (mferror.h).</summary>
    /// <summary>MF_E_NO_EVENTS_AVAILABLE（mferror.h）。</summary>
    public const int MfENoEventsAvailable = unchecked((int)0xC00D3E80);

    /// <summary>METransformNeedInput (mfobjects.h: 600 + 1).</summary>
    /// <summary>METransformNeedInput（mfobjects.h：600 + 1）。</summary>
    public const uint MeTransformNeedInput = 601;

    /// <summary>METransformHaveOutput.</summary>
    /// <summary>METransformHaveOutput。</summary>
    public const uint MeTransformHaveOutput = 602;

    /// <summary>METransformDrainComplete.</summary>
    /// <summary>METransformDrainComplete。</summary>
    public const uint MeTransformDrainComplete = 603;

    [LibraryImport(D3D11)]
    public static partial int D3D11CreateDevice(
        nint pAdapter,
        uint driverType,
        nint software,
        uint flags,
        nint pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out nint ppDevice,
        out uint pFeatureLevel,
        out nint ppImmediateContext);

    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleDescCount;
        public uint SampleDescQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MappedSubresource
    {
        public nint pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoProcessorInputViewDesc
    {
        public uint FourCC;
        public uint ViewDimension;
        public uint Texture2DMipSlice;
        public uint Texture2DArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoProcessorOutputViewDesc
    {
        public uint FourCC;
        public uint ViewDimension;
        public uint Texture2DMipSlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoProcessorStream
    {
        public int Enable;
        public int OutputIndex;
        public int InputFrameOrField;
        public int PastFrames;
        public int FutureFrames;
        public nint PastFramesPtr;
        public nint FutureFramesPtr;
        public nint InputFramePtr;
    }
}
