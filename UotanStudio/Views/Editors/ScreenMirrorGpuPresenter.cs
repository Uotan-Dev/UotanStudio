using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;

namespace UotanStudio.Views.Editors;

/// <summary>Uploads compact video planes to the existing Skia GPU context, like scrcpy's YUV texture renderer.</summary>
/// <remarks>Images are immutable. Scene operations retain them until the compositor releases them.
/// At most three frames may be live; backpressure drops only decoded presentation frames.</remarks>
internal sealed class ScreenMirrorGpuPresenter : IDisposable
{
    private readonly object _gate = new();
    private VideoImage? _latest;
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
        VideoImage next;
        try { next = new VideoImage(frame, matrix, range, Interlocked.Increment(ref _sequence), ReleaseSlot); }
        catch (Exception error)
        {
            ReleaseSlot(); _available = false; _failed = true; Failure = error.Message;
            return false;
        }
        VideoImage? old;
        lock (_gate)
        {
            old = _latest;
            if (!_closed) _latest = next;
            else { next.Release(); return true; }
        }
        old?.Release();
        return true;
    }

    private void ReleaseSlot() { lock (_gate) _liveFrames--; }

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
        VideoImage? old;
        lock (_gate) { _closed = true; _available = false; old = _latest; _latest = null; }
        old?.Release();
    }

    private sealed class DrawOperation(ScreenMirrorGpuPresenter owner, VideoImage? frame, Rect bounds) : ICustomDrawOperation
    {
        private VideoImage? _frame = frame;
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() => Interlocked.Exchange(ref _frame, null)?.Release();
        public void Render(ImmediateDrawingContext context)
        {
            if (owner._closed || owner._failed) return;
            try
            {
                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (feature is null) { owner._available = false; return; }
                using var lease = feature.Lease();
                if (lease.GrContext is null) { owner._available = false; return; }
                owner.Backend = $"Skia {lease.GrContext.Backend}";
                _ = VideoImage.Effect.Value; // Probe shader support before selecting NV12 readback.
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

    /// <summary>One ref-counted immutable copy of BGRA or NV12 planes. No full-size BGRA conversion for NV12.</summary>
    internal sealed class VideoImage
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
                    float2 uv = chroma.eval(floor(p * 0.5) + 0.5).rg * 255.0 - 128.0;
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
        private int _references = 1;
        internal int Width { get; }
        internal int Height { get; }
        internal long Sequence { get; }

        internal VideoImage(DecodedVideoFrame frame, VideoColorMatrix matrix, VideoColorRange range, long sequence, Action release)
        {
            Width = frame.Width; Height = frame.Height; Sequence = sequence; _release = release;
            _matrix = matrix == VideoColorMatrix.Auto ? frame.ColorMatrix : matrix;
            _range = range == VideoColorRange.Auto ? frame.ColorRange : range;
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
        internal void Retain() => Interlocked.Increment(ref _references);
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            _uv?.Dispose(); _first.Dispose(); _release();
        }
        internal void Draw(SKCanvas canvas, Rect bounds, double opacity = 1, GRContext? gpu = null)
        {
            float scale = (float)Math.Min(bounds.Width / Width, bounds.Height / Height);
            if (scale <= 0) return;
            // Video images change continuously. Do not fill the application's GPU cache
            // with obsolete frames; these uploads live only through this draw submission.
            using var uploadedFirst = gpu is null ? null : _first.ToTextureImage(gpu, false, false)
                ?? throw new InvalidOperationException("Video texture upload failed.");
            using var uploadedUv = gpu is null || _uv is null ? null : _uv.ToTextureImage(gpu, false, false)
                ?? throw new InvalidOperationException("Chroma texture upload failed.");
            var first = uploadedFirst ?? _first;
            var chroma = uploadedUv ?? _uv;
            canvas.Save();
            try
            {
                canvas.Translate((float)(bounds.X + (bounds.Width - Width * scale) / 2), (float)(bounds.Y + (bounds.Height - Height * scale) / 2));
                canvas.Scale(scale);
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255)) };
                if (chroma is null) { canvas.DrawImage(first, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint); return; }
                using var y = first.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
                using var uv = chroma.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
                var effect = Effect.Value;
                bool full = _range == VideoColorRange.Full, hd = _matrix == VideoColorMatrix.Bt709;
                // Match the existing converter's supported matrices/ranges, including its Auto defaults.
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
}