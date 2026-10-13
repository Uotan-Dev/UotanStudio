namespace UotanStudio.Messages;

/// <summary>
/// Carries the outcome of a settings save attempt.
/// <para>携带一次设置保存尝试的结果。</para>
/// </summary>
/// <param name="Succeeded">True when the save finished without error. <para>保存无错误完成时为 true。</para></param>
/// <param name="Error">Failure text; null when the save succeeded. <para>失败文本；保存成功时为 null。</para></param>
internal sealed record SettingsSaveCompletedMessage(bool Succeeded, string? Error);