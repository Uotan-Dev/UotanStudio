namespace UotanStudio.Compress.Internal;

/// <summary>
/// Creates unique scratch-file paths inside the system temporary directory for codecs that spool an intermediate
/// payload to disk.
/// <para>为需要把中间载荷暂存到磁盘的编解码器，在系统临时目录内创建唯一的刮擦文件路径。</para>
/// </summary>
/// <remarks>
/// This factory exists because <c>UotanStudio.Compress</c> sits below the firmware shared kernel and therefore
/// cannot call that kernel's own temporary-file helper. The naming convention is deliberately identical to the
/// one used by the firmware library, so scratch files keep their existing shape and stay recognisable as
/// transient artefacts.
/// <para>该工厂存在的原因是 <c>UotanStudio.Compress</c> 位于固件共享内核之下，无法调用内核自身的临时文件辅助方法。
/// 命名约定刻意与固件库保持一致，使刮擦文件维持既有形态并可被识别为瞬态产物。</para>
/// </remarks>
internal static class ScratchFile
{
    /// <summary>Builds a unique scratch path inside the system temporary directory with the given prefix and extension.
    /// <para>在系统临时目录内按给定前缀与扩展名构造唯一的刮擦路径。</para></summary>
    /// <param name="prefix">File-name prefix placed before the GUID; an empty value leaves a name that starts with the separating dash.
    /// <para>置于 GUID 之前的文件名前缀；为空时名称以分隔短横线开头。</para></param>
    /// <param name="extension">File-name suffix appended after the GUID, for example <c>.tmp</c>.
    /// <para>追加在 GUID 之后的文件名后缀，例如 <c>.tmp</c>。</para></param>
    /// <returns>A path inside the system temporary directory built from <paramref name="prefix"/>, a fresh GUID and <paramref name="extension"/>; no file is created.
    /// <para>系统临时目录内由 <paramref name="prefix"/>、新 GUID 与 <paramref name="extension"/> 组成的路径，不会创建文件。</para></returns>
    public static string CreateScratchPath(string prefix, string extension = ".tmp")
        => Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}{extension}");
}