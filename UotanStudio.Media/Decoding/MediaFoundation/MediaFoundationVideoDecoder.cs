using System.Runtime.InteropServices;

using System.Runtime.Versioning;

namespace UotanStudio.Media.Decoding.MediaFoundation;

/// <summary>
/// Push-style H.264/H.265 decoder built on the MediaFoundation MFT pipeline
/// (MFTEnumEx → IMFTransform → ProcessInput/ProcessOutput), emitting NV12
/// frames through the callback registered in <see cref="Initialize"/>.
/// <para>
/// 基于 MediaFoundation MFT 管线（MFTEnumEx → IMFTransform →
/// ProcessInput/ProcessOutput）的推送式 H.264/H.265 解码器，经
/// <see cref="Initialize"/> 注册的回调输出 NV12 帧。
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// The decoder drains output eagerly after each input sample so it works in
/// both async-MFT and sync-MFT modes. A format change (resolution change)
/// reported by <c>ProcessOutput</c> is resolved by re-reading the output type
/// and resetting the internal stride/dimension cache.
/// </para>
/// <para>
/// 解码器在每次输入后立即排空输出，因此同时兼容异步 MFT 与同步 MFT 模式。
/// ProcessOutput 报告的格式变更（分辨率变更）会通过重新读取输出类型并重置
/// 内部 stride/尺寸缓存来处理。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationVideoDecoder : IVideoDecoder
{
    private const uint OutputStreamId = 0;

    private readonly bool _ownsMfStartup;

    private IMFTransform? _transform;
    private Action<DecodedVideoFrame>? _frameCallback;
    private VideoCodecId _codec;
    private int _width;
    private int _height;
    private int _stride = -1;
    private long _referenceTimestampTicks;
    private bool _initialized;

    /// <summary>Initializes MF once per process; the first instance owns shutdown.</summary>
    /// <summary>进程内仅初始化一次 MF；首个实例负责关闭。</summary>
    private static readonly object MfInitLock = new();
    private static bool _mfStarted;
    private static int _activeDecoders;

    /// <summary>Creates the decoder. COM apartment and MF startup are handled lazily.</summary>
    /// <summary>创建解码器。COM 单元与 MF 启动按需惰性处理。</summary>
    public MediaFoundationVideoDecoder()
    {
        _ownsMfStartup = EnsureMediaFoundationStarted();
        Interlocked.Increment(ref _activeDecoders);
    }

    /// <inheritdoc />
    public bool IsInitialized => _initialized;

    /// <inheritdoc />
    public void Initialize(VideoCodecId codec, int width, int height, Action<DecodedVideoFrame> frameCallback)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive. 帧尺寸必须为正。");
        }

        ArgumentNullException.ThrowIfNull(frameCallback);

        ReleaseTransform();

        _transform = CreateTransform(codec, width, height);
        _codec = codec;
        _width = width;
        _height = height;
        _stride = -1;
        _referenceTimestampTicks = 0;
        _frameCallback = frameCallback;
        _initialized = true;

        _ = _transform.ProcessMessage(MfInterop.MftMessageBeginStreaming, IntPtr.Zero);
        _ = _transform.ProcessMessage(MfInterop.MftMessageStartOfStream, IntPtr.Zero);
    }

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> accessUnit)
    {
        var transform = _transform;
        if (!_initialized || transform is null || accessUnit.IsEmpty)
        {
            throw new InvalidOperationException(
                "Decoder is not initialized. 解码器尚未初始化。");
        }

        var sample = CreateSample(accessUnit);
        try
        {
            var hr = transform.ProcessInput(0, sample, 0);
            if (hr != MfInterop.SOk && hr != MfInterop.MfENotAccepting)
            {
                Throw(hr, "ProcessInput");
            }

            DrainOutput(transform);
        }
        finally
        {
            _ = Marshal.ReleaseComObject(sample);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        var transform = _transform;
        if (transform is null)
        {
            return;
        }

        _ = transform.ProcessMessage(MfInterop.MftMessageFlush, IntPtr.Zero);
        _stride = -1;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ReleaseTransform();
        Interlocked.Decrement(ref _activeDecoders);
        if (_ownsMfStartup)
        {
            // The first instance owns MF lifetime; MFShutdown must wait until all
            // decoders are gone. In practice this instance is the last one when
            // the app tears down the decode loop sequentially, but guard anyway.
            // 首个实例持有 MF 生命周期；必须等全部解码器销毁后才能关闭。实践中
            // 解码循环顺序销毁时本实例已是最后一个，但此处仍加保护。
            if (Interlocked.CompareExchange(ref _activeDecoders, 0, 0) == 0)
            {
                lock (MfInitLock)
                {
                    if (_mfStarted)
                    {
                        _ = MfInterop.MFShutdown();
                        _mfStarted = false;
                    }
                }
            }
        }
    }

    private static bool EnsureMediaFoundationStarted()
    {
        lock (MfInitLock)
        {
            if (!_mfStarted)
            {
                _ = MfInterop.CoInitializeEx(IntPtr.Zero, MfInterop.CoInitMultithreaded);
                var hr = MfInterop.MFStartup(MfInterop.MfVersion, 0);
                if (hr < 0)
                {
                    Throw(hr, "MFStartup");
                }

                _mfStarted = true;
                return true;
            }
        }

        return false;
    }

    private static IMFTransform CreateTransform(VideoCodecId codec, int width, int height)
    {
        var formatGuid = codec switch
        {
            VideoCodecId.H264 => MfInterop.VideoFormatH264,
            VideoCodecId.H265 => MfInterop.VideoFormatHevc,
            _ => throw new ArgumentOutOfRangeException(nameof(codec), "Unsupported codec. 不支持的编码格式。"),
        };

        var inputType = CreateMediaType(formatGuid, width, height);
        try
        {
            var registerInfo = new MftRegisterTypeInfo(MfInterop.MediaTypeVideo, formatGuid);
            var hr = MfInterop.MFTEnumEx(
                MfInterop.CategoryVideoDecoder,
                MfInterop.MftEnumFlags,
                in registerInfo,
                IntPtr.Zero,
                out var activatesPtr,
                out var count);
            Throw(hr, "MFTEnumEx");
            if (count == 0)
            {
                throw new NotSupportedException(
                    $"No MediaFoundation decoder found for {codec}. 未找到支持 {codec} 的 MediaFoundation 解码器。");
            }

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var activatePtr = Marshal.ReadIntPtr(activatesPtr, i * IntPtr.Size);
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(activatePtr);
                    try
                    {
                        _ = activate.ActivateObject(MfInterop.IidImfTransform, out var obj);
                        if (obj is not IMFTransform transform)
                        {
                            _ = Marshal.ReleaseComObject(obj);
                            continue;
                        }

                        if (ConfigureTypes(transform, inputType))
                        {
                            return transform;
                        }

                        _ = Marshal.ReleaseComObject(transform);
                    }
                    finally
                    {
                        _ = Marshal.ReleaseComObject(activate);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(activatesPtr);
            }

            throw new NotSupportedException(
                $"No usable MediaFoundation {codec} decoder accepted the stream configuration. 没有可用的 MediaFoundation {codec} 解码器接受该码流配置。");
        }
        finally
        {
            _ = Marshal.ReleaseComObject(inputType);
        }
    }

    private static bool ConfigureTypes(IMFTransform transform, IMFMediaType inputType)
    {
        if (transform.SetInputType(0, inputType, 0) != MfInterop.SOk)
        {
            return false;
        }

        // Pick the first available output type that is NV12.
        // 选择第一个 NV12 输出类型。
        for (var index = 0u; ; index++)
        {
            var hr = transform.GetOutputAvailableType(0, index, out var candidate);
            if (hr != MfInterop.SOk)
            {
                return false;
            }

            try
            {
                if (candidate.GetGUID(MfInterop.MtSubtype, out var subtype) == MfInterop.SOk
                    && subtype == MfInterop.VideoFormatNv12
                    && transform.SetOutputType(0, candidate, 0) == MfInterop.SOk)
                {
                    return true;
                }
            }
            finally
            {
                _ = Marshal.ReleaseComObject(candidate);
            }
        }
    }

    private static IMFMediaType CreateMediaType(Guid subtype, int width, int height)
    {
        Throw(MfInterop.MFCreateMediaType(out var typePtr), "MFCreateMediaType");
        var type = (IMFMediaType)Marshal.GetObjectForIUnknown(typePtr);
        try
        {
            Throw(type.SetGUID(MfInterop.MtMajorType, MfInterop.MediaTypeVideo), "SetGUID(MAJOR_TYPE)");
            Throw(type.SetGUID(MfInterop.MtSubtype, subtype), "SetGUID(SUBTYPE)");
            var frameSize = ((ulong)(uint)width << 32) | (uint)height;
            Throw(type.SetUINT64(MfInterop.MtFrameSize, frameSize), "SetUINT64(FRAME_SIZE)");
            return type;
        }
        catch
        {
            _ = Marshal.ReleaseComObject(type);
            throw;
        }
    }

    private static IMFSample CreateSample(ReadOnlySpan<byte> data)
    {
        Throw(MfInterop.MFCreateMemoryBuffer((uint)data.Length, out var bufferPtr), "MFCreateMemoryBuffer");
        var buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(bufferPtr);
        try
        {
            Throw(buffer.Lock(out var dest, out _, out _), "Buffer.Lock");
            try
            {
                fixed (byte* src = data)
                {
                    Buffer.MemoryCopy((void*)dest, (void*)src, data.Length, data.Length);
                }
            }
            finally
            {
                _ = buffer.Unlock();
            }

            Throw(buffer.SetCurrentLength((uint)data.Length), "SetCurrentLength");

            Throw(MfInterop.MFCreateSample(out var samplePtr), "MFCreateSample");
            var sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePtr);
            try
            {
                Throw(sample.AddBuffer(buffer), "AddBuffer");
                return sample;
            }
            catch
            {
                _ = Marshal.ReleaseComObject(sample);
                throw;
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(buffer);
        }
    }

    private void DrainOutput(IMFTransform transform)
    {
        while (true)
        {
            var outputBuffer = new MftOutputDataBuffer { StreamId = OutputStreamId };
            var hr = transform.ProcessOutput(0, 1, ref outputBuffer, out var status);
            if (hr == MfInterop.MfETransformNeedMoreInput)
            {
                return;
            }

            if (hr == MfInterop.MfETransformStreamChange
                || (outputBuffer.Status & MfInterop.MftOutputDataBufferFormatChange) != 0)
            {
                ResolveOutputType(transform);
                continue;
            }

            Throw(hr, "ProcessOutput");

            var sample = outputBuffer.Sample;
            if (sample is null)
            {
                return;
            }

            try
            {
                EmitFrame(sample);
            }
            finally
            {
                _ = Marshal.ReleaseComObject(sample);
            }
        }
    }

    private void ResolveOutputType(IMFTransform transform)
    {
        Throw(transform.GetOutputCurrentType(0, out var type), "GetOutputCurrentType");
        try
        {
            if (type.GetUINT64(MfInterop.MtFrameSize, out var frameSize) == MfInterop.SOk)
            {
                _width = (int)(frameSize >> 32);
                _height = (int)(frameSize & 0xFFFFFFFF);
            }

            if (type.GetUINT32(MfInterop.MtDefaultStride, out var stride) == MfInterop.SOk && stride > 0)
            {
                _stride = (int)stride;
            }
            else
            {
                _stride = -1;
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(type);
        }
    }

    private void EmitFrame(IMFSample sample)
    {
        Throw(sample.ConvertToContiguousBuffer(out var buffer), "ConvertToContiguousBuffer");
        try
        {
            Throw(buffer.Lock(out var src, out _, out var length), "Buffer.Lock");
            try
            {
                var stride = _stride > 0
                    ? _stride
                    : ComputeDefaultStride(_width);

                var frameHeight = _height;
                var required = checked((int)((long)stride * frameHeight * 3 / 2));
                var copyLength = Math.Min((int)length, required);

                var pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(required);
                var frame = new DecodedVideoFrame(
                    pooled,
                    copyLength,
                    _width,
                    frameHeight,
                    stride,
                    _referenceTimestampTicks);
                try
                {
                    fixed (byte* dst = pooled)
                    {
                        Buffer.MemoryCopy(dst, (void*)src, copyLength, copyLength);
                    }

                    // Ownership contract: the decoder disposes the frame once the
                    // callback returns; consumers must copy or convert within it.
                    // 所有权契约：回调返回后由解码器释放帧；消费方须在回调内完成
                    // 复制或转换。
                    _frameCallback?.Invoke(frame);
                }
                finally
                {
                    frame.Dispose();
                }
            }
            finally
            {
                _ = buffer.Unlock();
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(buffer);
        }
    }

    private static int ComputeDefaultStride(int width)
        // NV12 default stride: width rounded up to 2 bytes (chroma pairs).
        // NV12 默认行距：宽度向上取整到 2 字节（色度对）。
        => (width + 1) & ~1;

    private void ReleaseTransform()
    {
        if (_transform is not null)
        {
            _ = _transform.ProcessMessage(MfInterop.MftMessageFlush, IntPtr.Zero);
            _ = Marshal.ReleaseComObject(_transform);
            _transform = null;
        }

        _initialized = false;
        _frameCallback = null;
    }

    private static void Throw(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException(
                $"MediaFoundation operation '{operation}' failed: 0x{hr:X8}. MediaFoundation 操作“{operation}”失败：0x{hr:X8}。");
        }
    }
}
