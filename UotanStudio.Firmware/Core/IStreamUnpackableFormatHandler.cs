namespace UotanStudio.Firmware.Core;

/// <summary>
/// Optional capability of a format handler that can unpack from an already-resolved logical stream rather
/// than from a file path. A container format uses it to cascade-unpack its payload without naming the inner
/// format's parser, so the inner format keeps exclusive ownership of its own layout knowledge.
/// <para>格式处理器的可选能力：从已解析好的逻辑流而非文件路径进行解包。容器格式借此级联解包其载荷，
/// 且无需指明内层格式的解析器，从而让内层格式独占自己的布局知识。</para>
/// </summary>
public interface IStreamUnpackableFormatHandler
{
    /// <summary>
    /// Determines whether the handler recognises the format carried by the logical stream, probing only the
    /// offsets the format's own layout defines.
    /// <para>判断处理器是否识别该逻辑流所承载的格式，仅探测该格式自身布局定义的偏移。</para>
    /// </summary>
    /// <param name="logical">Seekable logical stream positioned by the caller; the implementation restores
    /// the position before returning. <para>调用方定位好的可寻址逻辑流；实现须在返回前恢复其位置。</para></param>
    /// <returns><see langword="true"/> when this handler owns the stream content. <para>当该处理器拥有流内容时为 <see langword="true"/>。</para></returns>
    bool CanHandleLogicalStream(Stream logical);

    /// <summary>
    /// Unpacks the logical stream into the output directory. The returned outcome must carry the same entry
    /// list and metadata keys the file-path unpacking path produces, so a container caller can forward it
    /// unchanged.
    /// <para>把逻辑流解包到输出目录。返回的结果必须携带与文件路径解包路径相同的条目列表与元数据键，
    /// 以便容器调用方原样转发。</para>
    /// </summary>
    /// <param name="logical">Seekable logical stream of the format's own content. <para>该格式自身内容对应的可定位逻辑流。</para></param>
    /// <param name="outputDirectory">Destination directory receiving the extracted content. <para>接收解出内容的目标目录。</para></param>
    /// <param name="byteProgress">Optional byte-level progress source supplied by the container caller. <para>容器调用方提供的可选字节级进度源。</para></param>
    /// <param name="overwrite">Whether existing output files may be replaced. <para>是否允许替换已存在的输出文件。</para></param>
    /// <param name="cancellationToken">Cancels the extraction. <para>取消解包。</para></param>
    /// <returns>An outcome describing what was extracted. <para>描述本次解包产出的结果。</para></returns>
    Task<FirmwareOpResult> UnpackLogicalStreamAsync(Stream logical, string outputDirectory,
        IProgress<FileSystemByteProgress>? byteProgress, bool overwrite, CancellationToken cancellationToken);
}