using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using UotanStudio.Media.Decoding.VideoToolbox;

namespace UotanStudio.Views.Editors;

/// <summary>
/// Uploads compact video planes to the existing Skia GPU context and imports shared surfaces
/// through a composition drawing surface.
/// <para>把紧凑视频平面上传到既有 Skia GPU 上下文，并经合成绘图表面导入共享表面。</para>
/// </summary>
internal sealed class ScreenMirrorGpuPresenter : IDisposable
{
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<Action> _recycle = new();
    private PresentedImage? _latest;
    private int _liveFrames;
    private volatile bool _closed, _failed;
    private volatile bool _available;
    private long _sequence, _lastDrawn, _draws, _skipped;
    private ICompositionGpuInterop? _interop;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceVisual? _visual;
    private Visual? _hostControl;
    private int _compositionGeneration;
    private volatile bool _compositionFailed;
    private int _compositionProbeCompleted;
    private int _compositionWidth, _compositionHeight;
    private long _compositedFrames;
    private readonly Dictionary<nint, CompositionImport> _imports = new();
    internal bool IsAvailable => _available;
    internal long Draws => Interlocked.Read(ref _draws);
    internal long Skipped => Interlocked.Read(ref _skipped);
    internal long CompositedFrames => Interlocked.Read(ref _compositedFrames);
    internal int LiveFrames { get { lock (_gate) return _liveFrames; } }
    internal PixelSize PixelSize
    {
        get
        {
            lock (_gate)
            {
                if (_compositionWidth > 0) return new(_compositionWidth, _compositionHeight);
                return _latest is { } frame ? new(frame.Width, frame.Height) : default;
            }
        }
    }
    internal string? Failure { get; private set; }
    internal string Backend { get; private set; } = "Uninitialized";

    /// <summary>
    /// Snapshot of the compositor's external-image import capability taken when the video control attached;
    /// <see langword="null"/> before the first attach or when the compositor exposes no interop.
    /// <para>视频控件挂载时采样的合成器外部图像导入能力快照；首次挂载前或合成器无互操作时为
    /// <see langword="null"/>。</para>
    /// </summary>
    /// <value>
    /// The captured capability snapshot or <see langword="null"/>.
    /// <para>已采集的能力快照或 <see langword="null"/>。</para>
    /// </value>
    internal CompositionInterop? Composition { get; private set; }

    /// <summary>Raised after the composition presentation path failed; the session should disable GPU export.<para>合成呈现路径失败后触发；会话应禁用 GPU 导出。</para></summary>
    internal event EventHandler? CompositionFailed;

    /// <summary>
    /// Whether the composition interop probe has completed at least once (with or without capability);
    /// decode loops wait for it before choosing export options.
    /// <para>合成互操作探测是否已完成过一次（无论有无能力）；解码循环在选择导出选项前等待它。</para>
    /// </summary>
    /// <value>
    /// True once at least one probe has finished.
    /// <para>至少一次探测完成后的取值。</para>
    /// </value>
    internal bool CompositionProbeCompleted => Volatile.Read(ref _compositionProbeCompleted) != 0;

    /// <summary>
    /// Human-readable presentation path for diagnostics: composition, Skia GPU or bitmap.
    /// <para>用于诊断的呈现路径可读名称：合成、Skia GPU 或位图。</para>
    /// </summary>
    /// <value>
    /// The name of the active presentation path.
    /// <para>当前呈现路径的名称。</para>
    /// </value>
    internal string PresentationPath => UsesCompositionSurface ? "Composition (GPU zero-copy)"
        : _available ? Backend : "Bitmap";

    /// <summary>
    /// Whether composition-owned frames currently cover the video area.
    /// <para>当前是否由合成器持有的帧覆盖视频区域。</para>
    /// </summary>
    /// <value>
    /// True while a healthy composition surface is held.
    /// <para>持有健康的合成表面时为真。</para>
    /// </value>
    internal bool UsesCompositionSurface
    {
        get { lock (_gate) return _surface is not null && !_closed && !_compositionFailed; }
    }

    /// <summary>
    /// Probes the compositor's GPU interop and hosts a composition drawing surface inside
    /// <paramref name="host"/>; UI thread only. The result is exposed through <see cref="Composition"/>.
    /// <para>探测合成器 GPU 互操作并在 <paramref name="host"/> 内托管合成绘图表面；仅限 UI 线程。
    /// 结果经 <see cref="Composition"/> 暴露。</para>
    /// </summary>
    /// <param name="host">
    /// The visual whose compositor receives the probing and the hosted surface.
    /// <para>其合成器接受探测并承载该表面的可视对象。</para>
    /// </param>
    internal async Task AttachCompositionVisualAsync(Visual host)
    {
        Dispatcher.UIThread.VerifyAccess();
        DetachCompositionVisual();
        lock (_gate)
        {
            if (_closed || _failed) { Volatile.Write(ref _compositionProbeCompleted, 1); return; }
        }
        try
        {
            int generation = Volatile.Read(ref _compositionGeneration);
            var elementVisual = ElementComposition.GetElementVisual(host);
            if (elementVisual is null) return;
            var interop = await elementVisual.Compositor.TryGetCompositionGpuInterop();
            if (Volatile.Read(ref _compositionGeneration) != generation) return;
            if (interop is null) return;
            bool keyedMutex = interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle)
                && interop.GetSynchronizationCapabilities(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle)
                    .HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex);
            bool iosurface = interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef)
                && interop.GetSynchronizationCapabilities(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef)
                    .HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.Automatic);
            if (!keyedMutex && !iosurface) return;
            var surface = elementVisual.Compositor.CreateDrawingSurface();
            var visual = elementVisual.Compositor.CreateSurfaceVisual();
            visual.Surface = surface;
            visual.Visible = false;
            ElementComposition.SetElementChildVisual(host, visual);
            lock (_gate)
            {
                if (_closed)
                {
                    ElementComposition.SetElementChildVisual(host, null);
                    surface.Dispose();
                    return;
                }
                _interop = interop;
                _surface = surface;
                _visual = visual;
                _hostControl = host;
                _compositionGeneration++;
                Composition = new CompositionInterop(keyedMutex, iosurface, interop.DeviceLuid);
            }
        }
        catch (Exception error)
        {
            Services.LogService.Instance.Write(Services.LogLevel.Warn, "ScreenMirror",
                $"Composition interop probe failed; using NV12 readback: {error.Message}");
        }
        finally
        {
            Volatile.Write(ref _compositionProbeCompleted, 1);
        }
    }

    /// <summary>
    /// Removes the hosted composition surface and drops the capability snapshot; UI thread only.
    /// <para>移除托管的合成绘图表面并丢弃能力快照；仅限 UI 线程。</para>
    /// </summary>
    internal void DetachCompositionVisual()
    {
        Dispatcher.UIThread.VerifyAccess();
        CompositionDrawingSurface? surface;
        CompositionSurfaceVisual? visual;
        Visual? host;
        lock (_gate)
        {
            _compositionGeneration++;
            Composition = null;
            surface = _surface;
            _surface = null;
            visual = _visual;
            _visual = null;
            host = _hostControl;
            _hostControl = null;
            _interop = null;
        }
        if (host is not null) ElementComposition.SetElementChildVisual(host, null);
        if (visual is not null) visual.Visible = false;
        surface?.Dispose();
    }

    /// <summary>
    /// Re-letterboxes the composition surface after the container bounds or the video size changed; UI thread only.
    /// <para>容器边界或视频尺寸变化后重新按信箱模式布局合成表面；仅限 UI 线程。</para>
    /// </summary>
    internal void UpdateCompositionLayout()
    {
        Dispatcher.UIThread.VerifyAccess();
        CompositionSurfaceVisual? visual;
        Visual? host;
        int width, height;
        lock (_gate)
        {
            visual = _visual;
            host = _hostControl;
            width = _compositionWidth;
            height = _compositionHeight;
        }
        if (visual is null || host is null) return;
        var bounds = host.Bounds.Size;
        if (width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            visual.Visible = false;
            return;
        }
        double scale = Math.Min(bounds.Width / width, bounds.Height / height);
        if (double.IsInfinity(scale) || scale <= 0) return;
        visual.Size = new Vector(width * scale, height * scale);
        visual.Offset = new Vector3D((bounds.Width - width * scale) / 2, (bounds.Height - height * scale) / 2, 0);
        visual.Visible = UsesCompositionSurface;
    }

    /// <summary>
    /// Presents a Windows shared-texture frame through the composition surface; the frame's slot is
    /// always returned to the decoder ring, whether or not presentation succeeded.
    /// <para>经合成表面呈现 Windows 共享纹理帧；无论呈现成败，帧的槽位都会归还解码器环。</para>
    /// </summary>
    /// <param name="frame">
    /// The shared-texture frame taken from the decoder ring.
    /// <para>取自解码器环的共享纹理帧。</para>
    /// </param>
    /// <returns><see langword="true"/> when the frame was queued for presentation.<para>帧已入队呈现时为 <see langword="true"/>。</para></returns>
    [SupportedOSPlatform("windows")]
    internal bool PublishSharedTexture(D3D11SharedVideoFrame frame)
    {
        if (!UsesCompositionSurface)
        {
            frame.MarkPresented();
            return false;
        }
        Dispatcher.UIThread.Post(() => PresentSharedTexture(frame), DispatcherPriority.Render);
        return true;
    }

    /// <summary>
    /// Presents a macOS IOSurface frame through the composition surface; the pixel buffer is always
    /// released back to VideoToolbox's pool.
    /// <para>经合成表面呈现 macOS IOSurface 帧；像素缓冲总会归还 VideoToolbox 的缓冲池。</para>
    /// </summary>
    /// <param name="frame">
    /// The IOSurface frame taken from the pixel-buffer pool.
    /// <para>取自像素缓冲池的 IOSurface 帧。</para>
    /// </param>
    /// <returns><see langword="true"/> when the frame was queued for presentation.<para>帧已入队呈现时为 <see langword="true"/>。</para></returns>
    [SupportedOSPlatform("macos")]
    internal bool PublishIosurface(IosurfaceVideoFrame frame)
    {
        if (!UsesCompositionSurface)
        {
            frame.MarkPresented();
            return false;
        }
        Dispatcher.UIThread.Post(() => PresentIosurface(frame), DispatcherPriority.Render);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private async void PresentSharedTexture(D3D11SharedVideoFrame frame)
    {
        int generation = Volatile.Read(ref _compositionGeneration);
        try
        {
            var interop = _interop;
            var surface = _surface;
            if (_closed || interop is null || surface is null || Volatile.Read(ref _compositionGeneration) != generation)
                return;
            var import = ObtainImport(interop, frame.SharedHandle, frame.Width, frame.Height,
                KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle);
            await surface.UpdateWithKeyedMutexAsync(import.Image, 1, 0).ConfigureAwait(true);
            if (Volatile.Read(ref _compositionGeneration) != generation) return;
            SetCompositionFrameSize(frame.Width, frame.Height);
            if (Interlocked.Increment(ref _compositedFrames) == 1)
                Services.LogService.Instance.Write(Services.LogLevel.Info, "ScreenMirror",
                    "Composition presentation active: D3D11 shared texture.");
        }
        catch (Exception error)
        {
            DisableComposition(error.Message);
        }
        finally
        {
            frame.MarkPresented();
        }
    }

    [SupportedOSPlatform("macos")]
    private async void PresentIosurface(IosurfaceVideoFrame frame)
    {
        int generation = Volatile.Read(ref _compositionGeneration);
        try
        {
            var interop = _interop;
            var surface = _surface;
            if (_closed || interop is null || surface is null || Volatile.Read(ref _compositionGeneration) != generation)
                return;
            var image = interop.ImportImage(new PlatformHandle(frame.Iosurface, KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef),
                new PlatformGraphicsExternalImageProperties
                {
                    Width = frame.Width,
                    Height = frame.Height,
                    Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                    TopLeftOrigin = true,
                });
            try
            {
                await surface.UpdateAsync(image).ConfigureAwait(true);
                if (Volatile.Read(ref _compositionGeneration) != generation) return;
                SetCompositionFrameSize(frame.Width, frame.Height);
                Interlocked.Increment(ref _compositedFrames);
            }
            finally { await image.DisposeAsync().ConfigureAwait(true); }
        }
        catch (Exception error)
        {
            DisableComposition(error.Message);
        }
        finally
        {
            frame.MarkPresented();
        }
    }

    private CompositionImport ObtainImport(ICompositionGpuInterop interop, nint handle, int width, int height, string handleType)
    {
        lock (_gate)
        {
            if (_imports.TryGetValue(handle, out var existing))
            {
                if (existing.Width == width && existing.Height == height) return existing;
                _imports.Remove(handle);
            }
        }
        var image = interop.ImportImage(new PlatformHandle(handle, handleType), new PlatformGraphicsExternalImageProperties
        {
            Width = width,
            Height = height,
            Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
            TopLeftOrigin = true,
        });
        var import = new CompositionImport { Image = image, Width = width, Height = height };
        lock (_gate)
        {
            _imports[handle] = import;
            while (_imports.Count > 16)
            {
                var candidate = _imports.Values.FirstOrDefault(item => item != import && item.Update.IsCompleted);
                if (candidate is null) break;
                _imports.Remove(_imports.First(pair => pair.Value == candidate).Key);
                _ = DisposeImportAsync(candidate);
            }
        }
        return import;
    }

    private void SetCompositionFrameSize(int width, int height)
    {
        lock (_gate)
        {
            _compositionWidth = width;
            _compositionHeight = height;
        }
        UpdateCompositionLayout();
    }

    private void DisableComposition(string reason)
    {
        bool raise;
        lock (_gate)
        {
            raise = !_compositionFailed;
            _compositionFailed = true;
            if (_visual is not null) _visual.Visible = false;
        }
        if (!raise) return;
        Services.LogService.Instance.Write(Services.LogLevel.Warn, "ScreenMirror",
            $"Composition presentation failed; using NV12 readback: {reason}");
        CompositionFailed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task DisposeImportAsync(CompositionImport import)
    {
        try { await import.Update.ConfigureAwait(false); }
        catch { }
        try { await import.Image.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    internal bool Publish(DecodedVideoFrame frame, VideoColorMatrix matrix, VideoColorRange range)
    {
        lock (_gate)
        {
            if (_closed) return false;
            if (_liveFrames >= 3) { Interlocked.Increment(ref _skipped); return true; }
            _liveFrames++;
        }
        PresentedImage next;
        try { next = new VideoImage(this, frame, matrix, range, Interlocked.Increment(ref _sequence), ReleaseSlot); }
        catch (Exception error)
        {
            ReleaseSlot(); _available = false; _failed = true; Failure = error.Message;
            return false;
        }
        ReplaceLatest(next);
        return true;
    }

    /// <summary>
    /// Publishes a zero-copy exported surface; the presenter owns the frame until recycled.
    /// <para>发布零拷贝导出表面；回收前由呈现器持有该帧。</para>
    /// </summary>
    /// <param name="frame">
    /// The exported dma-buf frame to retain until the next draw recycles it.
    /// <para>要保留到下一次绘制回收的导出 dma-buf 帧。</para>
    /// </param>
    /// <returns>
    /// True when the frame was consumed, false when the presenter is closed or failed.
    /// <para>帧被消费时为真，呈现器已关闭或失败时为假。</para>
    /// </returns>
    [SupportedOSPlatform("linux")]
    internal bool PublishDmabuf(VaDmaBufVideoFrame frame)
    {
        lock (_gate)
        {
            if (_closed || _failed) return false;
            if (_liveFrames >= 3)
            {
                Interlocked.Increment(ref _skipped);
                frame.Dispose();
                return true;
            }
            _liveFrames++;
        }
        PresentedImage next = new DmaBufVideoImage(this, frame, Interlocked.Increment(ref _sequence));
        ReplaceLatest(next);
        return true;
    }

    private void ReplaceLatest(PresentedImage next)
    {
        PresentedImage? old;
        lock (_gate)
        {
            old = _latest;
            if (!_closed) _latest = next;
            else next.Release();
        }
        old?.Release();
    }

    private void ReleaseSlot() { lock (_gate) _liveFrames--; }

    private void QueueRecycle(Action recycle) => _recycle.Enqueue(recycle);

    internal ICustomDrawOperation CreateOperation(Rect bounds)
    {
        lock (_gate)
        {
            _latest?.Retain();
            return new DrawOperation(this, _latest, bounds);
        }
    }

    public void Dispose()
    {
        PresentedImage? old;
        lock (_gate) { _closed = true; _available = false; old = _latest; _latest = null; }
        old?.Release();
        DisposeComposition();
    }

    private void DisposeComposition()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(DisposeComposition, DispatcherPriority.Render);
            return;
        }
        DetachCompositionVisual();
        CompositionImport[] imports;
        lock (_gate)
        {
            imports = [.. _imports.Values];
            _imports.Clear();
        }
        foreach (CompositionImport import in imports) _ = DisposeImportAsync(import);
    }

    private sealed class CompositionImport
    {
        public required ICompositionImportedGpuImage Image { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public Task Update { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// Snapshot of the compositor's external-image import capability.
    /// <para>合成器外部图像导入能力的快照。</para>
    /// </summary>
    /// <param name="WindowsKeyedMutex">
    /// The compositor imports global D3D11 shared textures synchronized by keyed mutex.
    /// <para>合成器可导入以键互锁同步的全局 D3D11 共享纹理。</para>
    /// </param>
    /// <param name="MacIosurface">
    /// The compositor imports IOSurfaces with automatic synchronization.
    /// <para>合成器可导入自动同步的 IOSurface。</para>
    /// </param>
    /// <param name="AdapterLuid">
    /// The compositor's 8-byte DXGI adapter LUID, when exposed.
    /// <para>合成器的 8 字节 DXGI 适配器 LUID（如暴露）。</para>
    /// </param>
    internal sealed record CompositionInterop(bool WindowsKeyedMutex, bool MacIosurface, byte[]? AdapterLuid);

    private sealed class DrawOperation(ScreenMirrorGpuPresenter owner, PresentedImage? frame, Rect bounds) : ICustomDrawOperation
    {
        private PresentedImage? _frame = frame;
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() => Interlocked.Exchange(ref _frame, null)?.Release();
        public void Render(ImmediateDrawingContext context)
        {

            while (owner._recycle.TryDequeue(out Action? recycle)) recycle();
            if (owner._closed || owner._failed || owner.UsesCompositionSurface) return;
            try
            {
                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (feature is null) { owner._available = false; return; }
                using var lease = feature.Lease();
                if (lease.GrContext is null) { owner._available = false; return; }
                owner.Backend = $"Skia {lease.GrContext.Backend}";
                _ = VideoImage.Effect.Value;
                if (!owner._closed) owner._available = true;
                if (_frame is not { } image) return;
                image.Draw(lease.SkCanvas, bounds, lease.CurrentOpacity, lease.GrContext);
                long previous = Interlocked.Read(ref owner._lastDrawn);
                while (image.Sequence > previous)
                {
                    long observed = Interlocked.CompareExchange(ref owner._lastDrawn, image.Sequence, previous);
                    if (observed == previous) { Interlocked.Increment(ref owner._draws); break; }
                    previous = observed;
                }
            }
            catch (Exception error)
            {
                owner._available = false;
                owner._failed = true;
                owner.Failure = error.Message;
                Services.LogService.Instance.Write(Services.LogLevel.Warn, "ScreenMirror", $"GPU presentation failed; using bitmap output: {error.Message}");
            }
        }
    }

    /// <summary>
    /// One ref-counted presentable frame; Release hands native resources back on the render thread.
    /// <para>一个引用计数的可呈现帧；Release 在渲染线程归还原生资源。</para>
    /// </summary>
    /// <param name="width">
    /// Frame width in pixels.
    /// <para>以像素计的帧宽。</para>
    /// </param>
    /// <param name="height">
    /// Frame height in pixels.
    /// <para>以像素计的帧高。</para>
    /// </param>
    /// <param name="sequence">
    /// Monotonic sequence number identifying the frame.
    /// <para>标识该帧的单调序列号。</para>
    /// </param>
    internal abstract class PresentedImage(int width, int height, long sequence)
    {
        private int _references = 1;
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal long Sequence { get; } = sequence;
        internal void Retain() => Interlocked.Increment(ref _references);
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            Recycle();
        }
        protected abstract void Recycle();
        public abstract void Draw(SKCanvas canvas, Rect bounds, double opacity, GRContext? gpu);
    }

    /// <summary>
    /// One ref-counted immutable copy of BGRA or NV12 planes. No full-size BGRA conversion for NV12.
    /// <para>BGRA 或 NV12 平面的一个引用计数不可变副本；NV12 不做全尺寸 BGRA 转换。</para>
    /// </summary>
    internal sealed class VideoImage : PresentedImage
    {
        internal static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
            SKRuntimeEffect.CreateShader("""
                uniform shader luma;
                uniform shader chroma;
                uniform float yOffset;
                uniform float yScale;
                uniform float4 coefficients;
                uniform float chromaSwap;
                half4 main(float2 p) {
                    float y = max(luma.eval(p).r * 255.0 - yOffset, 0.0) * yScale;
                    float2 cell = floor(p * 0.5);
                    float parity = mod(floor(p.y), 2.0);
                    float2 sampled = chroma.eval(float2(cell.x + 0.75, cell.y + 0.5 + parity * 0.5)).rg;
                    float2 uv = mix(sampled, sampled.yx, chromaSwap) * 255.0 - 128.0;
                    float3 rgb = float3(y + coefficients.x * uv.y,
                        y + coefficients.y * uv.x + coefficients.z * uv.y,
                        y + coefficients.w * uv.x);
                    return half4(clamp(rgb / 255.0, 0.0, 1.0), 1.0);
                }
                """, out var error) ?? throw new NotSupportedException(error));
        private readonly ScreenMirrorGpuPresenter? _owner;
        private readonly SKImage _first;
        private readonly SKImage? _uv;
        private readonly Action _release;
        private readonly VideoColorMatrix _matrix;
        private readonly VideoColorRange _range;
        private GRContext? _textureContext;
        private SKImage? _textureFirst, _textureUv;

        internal VideoImage(ScreenMirrorGpuPresenter? owner, DecodedVideoFrame frame, VideoColorMatrix matrix, VideoColorRange range, long sequence, Action release)
            : base(frame.Width, frame.Height, sequence)
        {
            _owner = owner;
            _matrix = matrix == VideoColorMatrix.Auto ? frame.ColorMatrix : matrix;
            _range = range == VideoColorRange.Auto ? frame.ColorRange : range;
            _release = release;
            if (frame.PixelFormat == VideoPixelFormat.Bgra32)
                _first = SKImage.FromPixelCopy(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque), frame.Span, frame.Stride)
                    ?? throw new NotSupportedException("Skia BGRA image creation failed.");
            else
            {
                _first = SKImage.FromPixelCopy(new SKImageInfo(Width, Height, SKColorType.R8Unorm, SKAlphaType.Opaque), frame.Span, frame.Stride)
                    ?? throw new NotSupportedException("Skia luma image creation failed.");
                try
                {
                    _uv = SKImage.FromPixelCopy(new SKImageInfo((Width + 1) / 2, (Height + 1) / 2, SKColorType.Rg88, SKAlphaType.Opaque),
                        frame.Span[(frame.Stride * Height)..], frame.Stride)
                        ?? throw new NotSupportedException("Skia chroma image creation failed.");
                }
                catch { _first.Dispose(); throw; }
            }
        }

        protected override void Recycle()
        {
            if (_textureContext is null || _owner is null)
            {
                _textureUv?.Dispose();
                _textureFirst?.Dispose();
                _uv?.Dispose();
                _first.Dispose();
                _release();
                return;
            }
            _owner.QueueRecycle(() =>
            {
                _textureUv?.Dispose();
                _textureFirst?.Dispose();
                _uv?.Dispose();
                _first.Dispose();
                _release();
            });
        }

        public override void Draw(SKCanvas canvas, Rect bounds, double opacity, GRContext? gpu)
        {
            float scale = (float)Math.Min(bounds.Width / Width, bounds.Height / Height);
            if (scale <= 0) return;
            SKImage first;
            SKImage? chroma;
            if (gpu is null)
            {
                first = _first;
                chroma = _uv;
            }
            else
            {
                if (!ReferenceEquals(_textureContext, gpu))
                {
                    _textureUv?.Dispose();
                    _textureFirst?.Dispose();
                    _textureFirst = _first.ToTextureImage(gpu, true, false)
                        ?? throw new InvalidOperationException("Video texture upload failed.");
                    _textureUv = _uv is null ? null : _uv.ToTextureImage(gpu, true, false)
                        ?? throw new InvalidOperationException("Chroma texture upload failed.");
                    _textureContext = gpu;
                }
                first = _textureFirst!;
                chroma = _textureUv;
            }
            canvas.Save();
            try
            {
                canvas.Translate((float)(bounds.X + (bounds.Width - Width * scale) / 2), (float)(bounds.Y + (bounds.Height - Height * scale) / 2));
                canvas.Scale(scale);
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255)) };
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                if (chroma is null) { canvas.DrawImage(first, 0, 0, sampling, paint); return; }
                PaintNv12(canvas, paint, first, chroma, sampling, _matrix, _range, chromaSwapped: false, Width, Height);
            }
            finally { canvas.Restore(); }
        }
    }

    /// <summary>
    /// Paints one NV12 frame with the shared runtime effect, taking every conversion constant from
    /// <see cref="Nv12ConversionTable"/> so the shader and the CPU converter cannot disagree.
    /// <para>使用共用的运行时着色器绘制一帧 NV12，所有转换常量都取自 <see cref="Nv12ConversionTable"/>，
    /// 从而保证着色器与 CPU 转换器不会不一致。</para>
    /// </summary>
    /// <param name="canvas">Canvas already translated and scaled to the frame rectangle.
    /// <para>已按帧矩形完成平移与缩放的画布。</para></param>
    /// <param name="paint">Paint carrying the target opacity; its shader is replaced here.
    /// <para>携带目标不透明度的画笔；此处替换其着色器。</para></param>
    /// <param name="luma">Luma plane image. <para>亮度平面图像。</para></param>
    /// <param name="chroma">Interleaved chroma plane image. <para>交错色度平面图像。</para></param>
    /// <param name="sampling">Sampling options for both planes. <para>两个平面的采样选项。</para></param>
    /// <param name="matrix">Chroma matrix to apply. <para>要应用的色度矩阵。</para></param>
    /// <param name="range">Nominal range to apply. <para>要应用的标称范围。</para></param>
    /// <param name="chromaSwapped">
    /// <see langword="true"/> when the chroma layer stores Cr before Cb, as a <c>GR88</c> DMA-BUF layer does;
    /// the shader then samples the two channels in the opposite order.
    /// <para>色度平面把 Cr 存在 Cb 之前（例如 <c>GR88</c> DMA-BUF 层）时为 <see langword="true"/>，
    /// 此时着色器按相反的通道顺序取值。</para>
    /// </param>
    /// <param name="width">Frame width in pixels. <para>帧宽度（像素）。</para></param>
    /// <param name="height">Frame height in pixels. <para>帧高度（像素）。</para></param>
    internal static void PaintNv12(SKCanvas canvas, SKPaint paint, SKImage luma, SKImage chroma, SKSamplingOptions sampling, VideoColorMatrix matrix, VideoColorRange range, bool chromaSwapped, int width, int height)
    {
        using var y = luma.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var uv = chroma.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        var effect = VideoImage.Effect.Value;
        var coefficients = Nv12ConversionTable.For(matrix, range);
        using var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["yOffset"] = (float)coefficients.LumaOffset,
            ["yScale"] = coefficients.LumaScale / 256f,
            ["chromaSwap"] = chromaSwapped ? 1f : 0f,
            ["coefficients"] = new float[]
            {
                coefficients.RedFromChromaRed / 256f,
                coefficients.GreenFromChromaBlue / 256f,
                coefficients.GreenFromChromaRed / 256f,
                coefficients.BlueFromChromaBlue / 256f,
            },
        };
        using var children = new SKRuntimeEffectChildren(effect) { ["luma"] = y, ["chroma"] = uv };
        using var shader = effect.ToShader(uniforms, children);
        paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, paint);
    }

    /// <summary>
    /// Presents an exported VA surface without downloading it: layers are imported lazily on the render thread.
    /// <para>呈现导出的 VA 表面而不下载它：各图层在渲染线程上惰性导入。</para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    private sealed class DmaBufVideoImage : PresentedImage
    {
        private const uint GlR8 = 0x8229;
        private const uint GlRg8 = 0x822B;
        private readonly ScreenMirrorGpuPresenter _owner;
        private readonly VaDmaBufVideoFrame _frame;
        private readonly Action _release;
        private readonly List<uint> _textures = [];
        private EglDmaBufImage? _lumaEgl, _chromaEgl;
        private SKImage? _luma, _chroma;
        private bool _chromaSwapped;
        private int _destroyed;

        internal DmaBufVideoImage(ScreenMirrorGpuPresenter owner, VaDmaBufVideoFrame frame, long sequence)
            : base(frame.Width, frame.Height, sequence)
        {
            _owner = owner;
            _frame = frame;
            _release = owner.ReleaseSlot;
        }

        protected override void Recycle() => _owner.QueueRecycle(DestroyNative);

        public override void Draw(SKCanvas canvas, Rect bounds, double opacity, GRContext? gpu)
        {
            float scale = (float)Math.Min(bounds.Width / Width, bounds.Height / Height);
            if (scale <= 0) return;
            if (gpu is null) throw new NotSupportedException("Zero-copy presentation requires a Skia GPU context.");
            try { EnsureImported(gpu); }
            catch (ObjectDisposedException) { return; }
            canvas.Save();
            try
            {
                canvas.Translate((float)(bounds.X + (bounds.Width - Width * scale) / 2), (float)(bounds.Y + (bounds.Height - Height * scale) / 2));
                canvas.Scale(scale);
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255)) };
                PaintNv12(canvas, paint, _luma!, _chroma!, new SKSamplingOptions(SKFilterMode.Nearest),
                    _frame.ColorMatrix, _frame.ColorRange, _chromaSwapped, Width, Height);
            }
            finally { canvas.Restore(); }
        }

        private void EnsureImported(GRContext gpu)
        {
            if (_luma is not null) return;
            try
            {
                var surface = _frame.Surface;
                _chromaSwapped = surface.ChromaOrder == DmabufChromaOrder.CrCb;
                var lumaEgl = EglDmaBufImage.Import(surface, 0);
                var chromaEgl = EglDmaBufImage.Import(surface, 1);
                _lumaEgl = lumaEgl;
                _chromaEgl = chromaEgl;
                var (lumaTexture, lumaTarget) = lumaEgl.CreateBoundTexture(externalOes: false, GlR8);
                var (chromaTexture, chromaTarget) = chromaEgl.CreateBoundTexture(externalOes: false, GlRg8);
                _textures.AddRange([lumaTexture, chromaTexture]);

                _luma = SKImage.FromTexture(gpu,
                        new GRBackendTexture(_frame.Width, _frame.Height, false, new GRGlTextureInfo { Id = lumaTexture, Target = lumaTarget, Format = GlR8 }),
                        GRSurfaceOrigin.TopLeft, SKColorType.R8Unorm)
                    ?? throw new NotSupportedException("Skia rejected the luma texture wrap.");
                _chroma = SKImage.FromTexture(gpu,
                        new GRBackendTexture((_frame.Width + 1) / 2, (_frame.Height + 1) / 2, false, new GRGlTextureInfo { Id = chromaTexture, Target = chromaTarget, Format = GlRg8 }),
                        GRSurfaceOrigin.TopLeft, SKColorType.Rg88)
                    ?? throw new NotSupportedException("Skia rejected the chroma texture wrap.");
            }
            catch
            {
                DestroyNative();
                throw;
            }
        }

        internal void DestroyNative()
        {
            if (Interlocked.Exchange(ref _destroyed, 1) != 0) return;
            foreach (uint texture in _textures) EglDmaBufImage.DeleteTexture(texture);
            _textures.Clear();
            _luma?.Dispose();
            _chroma?.Dispose();
            _lumaEgl?.Dispose();
            _chromaEgl?.Dispose();
            _luma = _chroma = null;
            _lumaEgl = _chromaEgl = null;
            _frame.Dispose();
            _release();
        }
    }
}