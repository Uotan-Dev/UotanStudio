using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Services.Compare;

/// <summary>One file's metadata, with no document, localization or display-row dependency.</summary>
/// <param name="FormatName">Detected format name; empty for unrecognized files.</param>
/// <param name="Summary">The format handler's summary.</param>
/// <param name="Metadata">The format handler's metadata values.</param>
/// <param name="Entries">Named entries reported by the handler.</param>
/// <param name="Components">Components reported by the handler.</param>
internal sealed record ComparisonMetadata(
    string FormatName,
    string Summary,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<string> Entries,
    IReadOnlyList<FirmwareComponent> Components)
{
    /// <summary>Placeholder for an unknown format or a handler-reported read failure.</summary>
    internal static ComparisonMetadata Unrecognized { get; } =
        new(string.Empty, string.Empty, new Dictionary<string, string>(), [], []);
}
