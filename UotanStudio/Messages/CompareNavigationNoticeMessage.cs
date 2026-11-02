namespace UotanStudio.Messages;

/// <summary>
/// Requests a localized navigation notice in the originating comparison view.
/// <para>请求在发起导航的对比视图中显示本地化提示。</para>
/// </summary>
internal sealed record CompareNavigationNoticeMessage(object Source, string Text);