namespace UotanStudio.Messages;

/// <summary>
/// <para>请求打开「添加子项目」表单的消息：由项目资源管理器右键发起，由 App 转发到
/// <c>MenuViewModel.AddSubProjectCommand</c>（子项目模式的新建窗口，默认位置为解决方案根目录）。</para>
/// Message requesting to open the add-sub-project form: raised by the project explorer context menu
/// and forwarded by App to <c>MenuViewModel.AddSubProjectCommand</c> (the new-project window in
/// sub-project mode; the default location is the solution root).
/// </summary>
public sealed class AddSubProjectRequestMessage
{
}