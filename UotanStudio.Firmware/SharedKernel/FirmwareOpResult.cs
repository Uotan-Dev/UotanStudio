namespace UotanStudio.Firmware.SharedKernel;

public sealed class FirmwareOpResult
{
    public bool Success { get; init; }

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string> Entries { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<FirmwareComponent> Components { get; init; } = [];

    /// <summary>
    /// Creates a success result.
    /// <para>创建成功结果。</para>
    /// </summary>
    public static FirmwareOpResult Ok(string summary, IReadOnlyList<string>? entries = null, IReadOnlyDictionary<string, string>? metadata = null, IReadOnlyList<FirmwareComponent>? components = null)
        => new()
        {
            Success = true,
            Summary = summary,
            Entries = entries ?? [],
            Metadata = metadata ?? new Dictionary<string, string>(),
            Components = components ?? []
        };

    /// <summary>
    /// Creates a failure result.
    /// <para>创建失败结果。</para>
    /// </summary>
    public static FirmwareOpResult Fail(string summary)
        => new() { Success = false, Summary = summary };
}