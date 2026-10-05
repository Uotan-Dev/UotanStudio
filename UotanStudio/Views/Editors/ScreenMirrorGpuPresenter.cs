using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;

namespace UotanStudio.Views.Editors;

/// <summary>Uploads compact video planes to the existing Skia GPU context, like scrcpy's YUV renderer.</summary>
/// <summary>把紧凑视频平面上传到既有 Skia GPU 上下文，类似 scrcpy 的 YUV 渲染器。</summary>
/// <remarks>
/// <para>
/// Images are immutable. Scene operations retain them until the compositor releases them.
/// At most three frames may be live; backpressure drops only decoded presentation frames.
/// Zero-copy <see cref="VaDmaBufVideoFrame"/> surfaces are imported on the render thread and their
/// native resources are recycled on the next draw; if a session closes before that flush, the
/// residual GL textures are bounded by the ring depth and the VA surface itself is released by the
/// decoder reset.
/// </para>
/// <para>
/// 图像不可变。场景操作会保留它们直到合成器释放。至多三帧存活；背压只丢弃解码呈现帧。
/// 零拷贝的 <see cref="VaDmaBufVideoFrame"/> 表面在渲染线程导入，其原生资源在下一次绘制时回收；
/// 若会话在回收前关闭，残留 GL 纹理以环深度为界，VA 表面本身由解码器重置释放。
/// </para>
/// </remarks>
internal sealed class ScreenMirrorGpuPresenter : IDisposable
{
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<Action> _recycle = new();
    private PresentedImage? _latest;
    private int _liveFrames;
    private volatile bool _closed, _failed;
    private volatile bool _available;
    private long _sequence, _lastDrawn, _draws, _skipped;
    internal bool IsAvailable => _available;
    internal long Draws => Interlocked.Read(ref _draws);
    internal long Skipped => Interlocked.Read(ref _skipped);
    internal int LiveFrames { get { lock (_gate) return _liveFrames; } }
    internal PixelSize PixelSize { get { lock (_gate) return _latest is { } frame ? new(frame.Width, frame.Height) : default; } }
    internal string? Failure { get; private set; }
    internal string Backend { get; private set; } = "Uninitialized";

    internal bool Publish(DecodedVideoFrame frame, VideoColorMatrix matrix, VideoColorRange range)
    {
        lock (_gate)
        {
            if (_closed) return false;
            if (_liveFrames >= 3) { Interlocked.Increment(ref _skipped); return true; }
            _liveFrames++;
        }
        PresentedImage next;
        try { next = new VideoImage(frame, matrix, range, Interlocked.Increment(ref _sequence), ReleaseSlot); }
        catch (Exception error)
        {
            ReleaseSlot(); _available = false; _failed = true; Failure = error.Message;
            return false;
        }
        ReplaceLatest(next);
        return true;
    }

    /// <summary>Publishes a zero-copy exported surface; the presenter owns the frame until recycled.</summary>
    /// <summary>发布零拷贝导出表面；回收前由呈现器持有该帧。</summary>
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
    }

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
            if (owner._closed || owner._failed) return;
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

    /// <summary>One ref-counted presentable frame; Release hands native resources back on the render thread.</summary>
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

    /// <summary>One ref-counted immutable copy of BGRA or NV12 planes. No full-size BGRA conversion for NV12.</summary>
    internal sealed class VideoImage : PresentedImage
    {
        internal static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
            SKRuntimeEffect.CreateShader("""
                uniform shader luma;
                uniform shader chroma;
                uniform float yOffset;
                uniform float yScale;
                uniform float4 coefficients;
                half4 main(float2 p) {
                    float y = max(luma.eval(p).r * 255.0 - yOffset, 0.0) * yScale;
                    float2 cell = floor(p * 0.5);
                    float parity = mod(floor(p.y), 2.0);
                    float2 uv = chroma.eval(float2(cell.x + 0.75, cell.y + 0.5 + parity * 0.5)).rg * 255.0 - 128.0;
                    float3 rgb = float3(y + coefficients.x * uv.y,
                        y + coefficients.y * uv.x + coefficients.z * uv.y,
                        y + coefficients.w * uv.x);
                    return half4(clamp(rgb / 255.0, 0.0, 1.0), 1.0);
                }
                """, out var error) ?? throw new NotSupportedException(error));
        private readonly SKImage _first;
        private readonly SKImage? _uv;
        private readonly Action _release;
        private readonly VideoColorMatrix _matrix;
        private readonly VideoColorRange _range;

        internal VideoImage(DecodedVideoFrame frame, VideoColorMatrix matrix, VideoColorRange range, long sequence, Action release)
            : base(frame.Width, frame.Height, sequence)
        {
            _matrix = matrix == VideoColorMatrix.Auto ? frame.ColorMatrix : matrix;
            _range = range == VideoColorRange.Auto ? frame.ColorRange : range;
            _release = release;
            if (frame is GpuDecodedVideoFrame)
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
            _uv?.Dispose();
            _first.Dispose();
            _release();
        }

        public override void Draw(SKCanvas canvas, Rect bounds, double opacity, GRContext? gpu)
        {
            float scale = (float)Math.Min(bounds.Width / Width, bounds.Height / Height);
            if (scale <= 0) return;

            using var uploadedFirst = gpu is null ? null : _first.ToTextureImage(gpu, true, false)
                ?? throw new InvalidOperationException("Video texture upload failed.");
            using var uploadedUv = gpu is null || _uv is null ? null : _uv.ToTextureImage(gpu, true, false)
                ?? throw new InvalidOperationException("Chroma texture upload failed.");
            var first = uploadedFirst ?? _first;
            var chroma = uploadedUv ?? _uv;
            canvas.Save();
            try
            {
                canvas.Translate((float)(bounds.X + (bounds.Width - Width * scale) / 2), (float)(bounds.Y + (bounds.Height - Height * scale) / 2));
                canvas.Scale(scale);
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255)) };
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
                if (chroma is null) { canvas.DrawImage(first, 0, 0, sampling, paint); return; }
                using var y = first.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
                using var uv = chroma.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
                var effect = Effect.Value;
                bool full = _range == VideoColorRange.Full, hd = _matrix == VideoColorMatrix.Bt709;

                using var uniforms = new SKRuntimeEffectUniforms(effect)
                {
                    ["yOffset"] = full ? 0f : 16f,
                    ["yScale"] = full ? 1f : 298f / 256,
                    ["coefficients"] = full
                        ? hd ? new float[] { 403f / 256, -48f / 256, -120f / 256, 475f / 256 } : [359f / 256, -88f / 256, -183f / 256, 454f / 256]
                        : hd ? new float[] { 459f / 256, -55f / 256, -136f / 256, 541f / 256 } : [409f / 256, -100f / 256, -208f / 256, 516f / 256],
                };
                using var children = new SKRuntimeEffectChildren(effect) { ["luma"] = y, ["chroma"] = uv };
                using var shader = effect.ToShader(uniforms, children);
                paint.Shader = shader;
                canvas.DrawRect(0, 0, Width, Height, paint);
            }
            finally { canvas.Restore(); }
        }
    }

    /// <summary>Presents an exported VA surface without downloading it: layers are imported lazily on the render thread.</summary>
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
            EnsureImported(gpu);
            canvas.Save();
            try
            {
                canvas.Translate((float)(bounds.X + (bounds.Width - Width * scale) / 2), (float)(bounds.Y + (bounds.Height - Height * scale) / 2));
                canvas.Scale(scale);
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255)) };
                using var y = _luma!.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
                using var uv = _chroma!.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
                var effect = VideoImage.Effect.Value;
                bool full = _frame.ColorRange == VideoColorRange.Full, hd = _frame.ColorMatrix == VideoColorMatrix.Bt709;
                using var uniforms = new SKRuntimeEffectUniforms(effect)
                {
                    ["yOffset"] = full ? 0f : 16f,
                    ["yScale"] = full ? 1f : 298f / 256,
                    ["coefficients"] = full
                        ? hd ? new float[] { 403f / 256, -48f / 256, -120f / 256, 475f / 256 } : [359f / 256, -88f / 256, -183f / 256, 454f / 256]
                        : hd ? new float[] { 459f / 256, -55f / 256, -136f / 256, 541f / 256 } : [409f / 256, -100f / 256, -208f / 256, 516f / 256],
                };
                using var children = new SKRuntimeEffectChildren(effect) { ["luma"] = y, ["chroma"] = uv };
                using var shader = effect.ToShader(uniforms, children);
                paint.Shader = shader;
                canvas.DrawRect(0, 0, Width, Height, paint);
            }
            finally { canvas.Restore(); }
        }

        private void EnsureImported(GRContext gpu)
        {
            if (_luma is not null) return;
            try
            {
                var lumaEgl = EglDmaBufImage.Import(_frame.Surface, 0);
                var chromaEgl = EglDmaBufImage.Import(_frame.Surface, 1);
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