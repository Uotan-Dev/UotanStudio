namespace UotanStudio.Models;

/// <summary>
/// <para>开发成员清单（设置页「开发成员」区块数据源，静态只读，AOT 友好）。</para>
/// Developer catalog (data source for the settings page's developer section; static and read-only,
/// AOT friendly).
/// </summary>
public static class DevelopmentMembers
{
    /// <summary>
    /// <para>获取开发成员清单。</para>
    /// Gets the developer catalog.
    /// </summary>
    public static IReadOnlyList<DevelopmentMember> All { get; } =
    [
        new DevelopmentMember
        {
            Name = "暮间雾",
            EnglishName = "mujianwu",
            Description = "项目负责人、UI与UX设计开发",
            AvatarUri = "avares://UotanStudio/Assets/mujianwu.jpg",
            GitHubUrl = "https://github.com/mujianwu",
            CoolapkUrl = "http://www.coolapk.com/u/3242606"
        },
        new DevelopmentMember
        {
            Name = "13584452567",
            Description = "项目后端开发，最强后端支持！",
            AvatarUri = "avares://UotanStudio/Assets/zicai.jpeg",
            GitHubUrl = "https://github.com/13584452567",
            CoolapkUrl = "https://www.coolapk.com/u/13898619"
        }
    ];
}