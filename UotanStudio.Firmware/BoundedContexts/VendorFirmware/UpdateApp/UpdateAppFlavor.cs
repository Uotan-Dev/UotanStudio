namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.UpdateApp;

/// <summary>
/// Classifies an <c>update.app</c> container by the header generation its payload entries use.
/// Huawei-verified containers carry a canonical header with a per-block CRC16 table; Unisoc/Spreadtrum-style
/// legacy containers use the shorter unverified header form. Both generations share the same 4-byte entry
/// signature and little-endian framing, so they are parsed by one code path and distinguished only by flavour.
/// <para>按载荷条目使用的头部代际对 <c>update.app</c> 容器分类。华为校验型容器使用带逐块 CRC16 表的标准头部；
/// Unisoc/展锐风格的旧式容器使用更短的未校验头部。两代条目共用同一 4 字节签名与小端序帧结构，
/// 因此由同一代码路径解析，仅按风格区分。</para>
/// </summary>
public enum UpdateAppFlavor
{
    /// <summary>The container exposes no named payload entry, so no flavour can be inferred.
    /// <para>容器不含任何命名载荷条目，无法推断风格。</para></summary>
    NotApplicable,

    /// <summary>Every named entry uses the canonical Huawei header with a non-zero CRC16 block size.
    /// <para>所有命名条目均使用带非零 CRC16 分块大小的华为标准头部。</para></summary>
    HuaweiVerified,

    /// <summary>Every named entry uses the legacy short-header form without a CRC16 table, as read by
    /// Unisoc/Spreadtrum-style <c>update.app</c> splitters.
    /// <para>所有命名条目均使用无 CRC16 表的旧式短头部，即 Unisoc/展锐风格 <c>update.app</c> 拆分器读取的形式。</para></summary>
    UnisocLegacy,

    /// <summary>Named entries mix the canonical and legacy header generations.
    /// <para>命名条目混合了标准头部与旧式短头部两代形式。</para></summary>
    Mixed
}