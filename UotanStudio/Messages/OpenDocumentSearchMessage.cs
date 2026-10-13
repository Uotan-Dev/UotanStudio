namespace UotanStudio.Messages;

/// <summary>
/// Requests a find or find-and-replace run for the current document.
/// <para>请求对当前文档执行查找或查找替换。</para>
/// </summary>
/// <param name="ShowReplace">True to allow replacing matches; false for search only. <para>为 true 时允许替换匹配项，为 false 时仅查找。</para></param>
public sealed record OpenDocumentSearchMessage(bool ShowReplace);