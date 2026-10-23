namespace UotanStudio.Compress.Zip;

/// <summary>
/// Bounds that <see cref="ZipContainer"/> applies while opening or creating a ZIP archive.
/// <para><see cref="ZipContainer"/> 在打开或创建 ZIP 归档时施加的上限。</para>
/// </summary>
/// <param name="MaxEntries">Maximum number of central-directory entries; <see langword="null"/> applies no count bound.
/// <para>中央目录条目数上限；为 <see langword="null"/> 时不限制条目数。</para></param>
/// <param name="MaxEntryBytes">Maximum uncompressed size of a single entry; <see langword="null"/> applies no per-entry bound.
/// <para>单个条目的非压缩大小上限；为 <see langword="null"/> 时不限制单条大小。</para></param>
/// <param name="MaxTotalBytes">Maximum sum of all uncompressed entry sizes; <see langword="null"/> applies no total bound.
/// <para>全部条目非压缩大小之和的上限；为 <see langword="null"/> 时不限制总量。</para></param>
public sealed record ZipContainerOptions(int? MaxEntries = null, long? MaxEntryBytes = null, long? MaxTotalBytes = null);