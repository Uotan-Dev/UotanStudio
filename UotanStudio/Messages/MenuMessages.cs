namespace UotanStudio.Messages;

/// <summary>
/// <para>标签页导航消息，用于在主视图的标签页之间切换。</para>
/// Tab navigation message, used to switch between tabs in the main view.
/// </summary>
public class NavigateTabMessage
{
    /// <summary>目标标签页索引。</summary>
    public int TabIndex { get; }

    public NavigateTabMessage(int tabIndex)
    {
        TabIndex = tabIndex;
    }
}

/// <summary>
/// <para>主题切换消息，从菜单触发。</para>
/// Theme switch message, triggered from the menu.
/// </summary>
public class MenuThemeChangedMessage
{
    public Models.ThemeMode ThemeMode { get; }

    public MenuThemeChangedMessage(Models.ThemeMode themeMode)
    {
        ThemeMode = themeMode;
    }
}

/// <summary>
/// <para>清空画布消息，由菜单触发，由主视图接收并调用 NodeEditorVM.ClearAllCommand。</para>
/// Clear canvas message, triggered from the menu, received by the main view which invokes NodeEditorVM.ClearAllCommand.
/// </summary>
public class ClearCanvasMessage
{
}
