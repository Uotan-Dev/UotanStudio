namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk;

public sealed class VectorDrawable
{
    public float Width { get; init; } = 24f;

    public float Height { get; init; } = 24f;

    public float ViewportWidth { get; init; } = 24f;

    public float ViewportHeight { get; init; } = 24f;

    public float Alpha { get; init; } = 1f;

    public IReadOnlyList<VectorPath> Paths { get; init; } = [];
}

public sealed class VectorPath
{
    public string PathData { get; init; } = string.Empty;

    public uint? FillColor { get; init; }

    public float FillAlpha { get; init; } = 1f;

    public uint? StrokeColor { get; init; }

    public float StrokeWidth { get; init; }

    public float StrokeAlpha { get; init; } = 1f;
}
