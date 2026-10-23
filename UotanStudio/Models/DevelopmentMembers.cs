namespace UotanStudio.Models;

/// <summary>
/// Static read-only catalog of development team members.
/// <para>静态只读的开发成员清单。</para>
/// </summary>
public static class DevelopmentMembers
{
    /// <summary>
    /// Gets the developer catalog.
    /// <para>获取开发成员清单。</para>
    /// </summary>
    /// <value>The read-only list of development team members.<para>只读的开发成员列表。</para></value>
    public static IReadOnlyList<DevelopmentMember> All { get; } =
    [
        new DevelopmentMember
        {
            Name = "暮间雾",
            EnglishName = "mujianwu",
            DescriptionKey = "Settings_Developer_LeadRole",
            AvatarUri = "avares://UotanStudio/Assets/mujianwu.jpg",
            GitHubUrl = "https://github.com/mujianwu",
            CoolapkUrl = "http://www.coolapk.com/u/3242606"
        },
        new DevelopmentMember
        {
            Name = "13584452567",
            DescriptionKey = "Settings_Developer_BackendRole",
            AvatarUri = "avares://UotanStudio/Assets/zicai.jpeg",
            GitHubUrl = "https://github.com/13584452567",
            CoolapkUrl = "https://www.coolapk.com/u/13898619"
        }
    ];
}