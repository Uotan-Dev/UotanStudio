namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Specifies how USB fastboot devices are matched during factory probing.
/// <para>指定工厂探测时 USB fastboot 设备的匹配方式。</para>
/// </summary>
internal enum FastbootMatchMode
{
    /// <summary>
    /// Only match devices exposing the standard fastboot interface
    /// (class 0xFF, subclass 0x42, protocol 0x03). This is the default and matches AOSP
    /// behavior; it never claims devices whose interface metadata is unavailable.
    /// <para>仅匹配暴露标准 fastboot 接口（class 0xFF、subclass 0x42、protocol 0x03）的设备。
    /// 这是默认行为，与 AOSP 一致；接口元数据不可用时绝不匹配。</para>
    /// </summary>
    InterfaceOnly = 0,

    /// <summary>
    /// Match the standard fastboot interface first; additionally accept devices listed in
    /// <see cref="FastbootFactory.KnownDeviceProfiles"/> by exact vendor/product id pair,
    /// even when their interface descriptor is not the standard fastboot one
    /// (e.g. legacy Amazon Kindle bootloaders).
    /// <para>优先匹配标准 fastboot 接口；此外按精确的厂商/产品 ID 对匹配
    /// <see cref="FastbootFactory.KnownDeviceProfiles"/> 中列出的设备，
    /// 即使其接口描述符并非标准 fastboot 接口（如老款 Amazon Kindle bootloader）。</para>
    /// </summary>
    InterfaceOrKnownVidPid = 1
}

/// <summary>
/// Describes a known fastboot-capable device identified by an exact vendor/product id pair,
/// used as a discovery fallback for devices with non-standard interface descriptors.
/// Matching is always on the full VID+PID pair, never on a whole vendor id, so unrelated
/// devices sharing a vendor (EDL, Rockchip loaders, etc.) are never claimed.
/// <para>描述一个已知支持 fastboot 协议、以精确厂商/产品 ID 对标识的设备，
/// 用于兜底发现接口描述符非标准的设备。始终按完整 VID+PID 对匹配，绝不按整个厂商 ID
/// 匹配，因此不会误认同厂商下的无关设备（EDL、Rockchip loader 等）。</para>
/// </summary>
/// <remarks>
/// Initializes a new fastboot device profile.
/// <para>初始化一个新的 fastboot 设备档案。</para>
/// </remarks>
internal sealed class FastbootDeviceProfile(ushort vendorId, ushort productId, string name)
{
    /// <summary>
    /// Gets the USB vendor id (VID).
    /// <para>获取 USB 厂商 ID（VID）。</para>
    /// </summary>
    public ushort VendorId { get; } = vendorId;

    /// <summary>
    /// Gets the USB product id (PID).
    /// <para>获取 USB 产品 ID（PID）。</para>
    /// </summary>
    public ushort ProductId { get; } = productId;

    /// <summary>
    /// Gets a human-readable name for the device family.
    /// <para>获取设备系列的易读名称。</para>
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Determines whether the given vendor/product id pair matches this profile.
    /// <para>判断给定的厂商/产品 ID 对是否匹配该档案。</para>
    /// </summary>
    public bool Matches(ushort vendorId, ushort productId)
        => VendorId == vendorId && ProductId == productId;
}

/// <summary>
/// Built-in known fastboot device profiles used by
/// <see cref="FastbootMatchMode.InterfaceOrKnownVidPid"/> as a discovery fallback.
/// <para>供 <see cref="FastbootMatchMode.InterfaceOrKnownVidPid"/> 用作发现兜底的
/// 内置已知 fastboot 设备档案。</para>
/// </summary>
internal static class FastbootDeviceProfiles
{
    /// <summary>
    /// Gets the built-in known fastboot device profiles. Every entry pins an exact
    /// VID+PID pair; whole-vendor wildcard entries are deliberately excluded so that
    /// non-fastboot devices sharing a vendor id are never claimed.
    /// <para>获取内置的已知 fastboot 设备档案。每个条目都固定精确的 VID+PID 对；
    /// 刻意排除整厂商通配条目，避免误认同厂商下的非 fastboot 设备。</para>
    /// </summary>
    public static IReadOnlyList<FastbootDeviceProfile> Default { get; } =
    [
        // Amazon Kindle Fire (1st gen, stock bootloader) exposes fastboot with a
        // non-standard interface descriptor in some firmware revisions.
        new FastbootDeviceProfile(0x1949, 0x0006, "Amazon Kindle Fire (stock bootloader)"),
        // Amazon Kindle Fire with FireFireFire bootloader uses the Google VID.
        new FastbootDeviceProfile(0x18D1, 0x0100, "Amazon Kindle Fire (FFF bootloader)")
    ];
}