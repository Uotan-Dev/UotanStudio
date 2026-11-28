namespace UotanStudio.Models;

/// <summary>
/// Static read-only catalog of friendly links.
/// <para>静态只读的友情链接清单。</para>
/// </summary>
public static class FriendlyLinks
{
    /// <summary>
    /// Gets the friendly link catalog.
    /// <para>获取友情链接清单。</para>
    /// </summary>
    /// <value>The read-only list of friendly links.<para>只读的友情链接列表。</para></value>
    public static IReadOnlyList<FriendlyLink> All { get; } =
    [
        new FriendlyLink
        {
            Name = "柚坛社区",
            DescriptionKey = "Settings_FriendlyLink_Community",
            AvatarUri = "avares://UotanStudio/Assets/uotan-community.png",
            Url = "https://www.uotan.cn"
        },
        new FriendlyLink
        {
            Name = "柚坛 UOTAN",
            DescriptionKey = "Settings_FriendlyLink_Bilibili",
            AvatarUri = "avares://UotanStudio/Assets/uotan-bilibili.png",
            Url = "https://space.bilibili.com/522676659"
        },
        new FriendlyLink
        {
            Name = "柚坛 Wiki",
            DescriptionKey = "Settings_FriendlyLink_Wiki",
            AvatarUri = "avares://UotanStudio/Assets/uotan-wiki.png",
            Url = "https://wiki.uotan.cn"
        }
    ];
}
