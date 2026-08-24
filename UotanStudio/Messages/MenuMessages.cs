namespace UotanStudio.Messages;

/// <summary>
/// Tab navigation Message, used to switch between tabs in the main view.
/// <para>标签页导航消息，用于在主视图的标签页之间切换。</para>
/// </summary>
public class NavigateTabMessage(int tabIndex)
{
    /// <summary>
    /// Gets the target tab index.
    /// <para>目标标签页索引。</para>
    /// </summary>
    public int TabIndex { get; } = tabIndex;
}

/// <summary>
/// Theme switch Message, triggered from the menu.
/// <para>主题切换消息，从菜单触发。</para>
/// </summary>
public class MenuThemeChangedMessage(Models.ThemeMode themeMode)
{
    /// <summary>
    /// Gets the theme mode.
    /// <para>主题模式。</para>
    /// </summary>
    public Models.ThemeMode ThemeMode { get; } = themeMode;
}

/// <summary>
/// Clear canvas Message, triggered from the menu, received by the main view which invokes NodeEditorVM.ClearAllCommand.
/// <para>清空画布消息，由菜单触发，由主视图接收并调用 NodeEditorVM.ClearAllCommand。</para>
/// </summary>
public class ClearCanvasMessage
{
}

/// <summary>
/// Project opened Message, broadcast after a project is opened or created; the window manager
/// uses it to auto-enable the project-related windows.
/// <para>工程打开消息，在工程被打开或新建后广播；窗口管理器据此自动启用与工程相关的窗口。</para>
/// </summary>
public class ProjectOpenedMessage
{
}