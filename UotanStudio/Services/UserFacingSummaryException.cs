namespace UotanStudio.Services;

/// <summary>
/// Carries a message that is already a complete, user-facing summary and must not be wrapped again.
/// <para>承载已经是完整用户摘要的消息，不得再被二次包装。</para>
/// </summary>
internal sealed class UserFacingSummaryException(string message) : IOException(message);