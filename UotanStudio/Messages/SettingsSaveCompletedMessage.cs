namespace UotanStudio.Messages;

/// <summary>
/// Carries the actual settings save result to the settings view.
/// </summary>
internal sealed record SettingsSaveCompletedMessage(bool Succeeded, string? Error);