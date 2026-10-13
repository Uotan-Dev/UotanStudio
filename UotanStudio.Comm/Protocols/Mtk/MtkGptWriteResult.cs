namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Evidence from one completed GPT write-back: how much was programmed and which equivalences were observed.
/// <para>来自一次已完成 GPT 写回的证据：编程了多少内容，以及观察到哪些等价性。</para>
/// </summary>
/// <remarks>
/// <para>The three equivalences are reported separately because they answer different questions and none of
/// them implies the others. <see cref="BytesEquivalent"/> says every programmed byte was read back identical;
/// <see cref="StructureEquivalent"/> says the table re-parsed from the device after the write carries the same
/// metadata fingerprint, block size and block count as the snapshot that was written;
/// <see cref="CapacityEquivalent"/> says the table's declared capacity still equals the region's reported
/// bytes. The programmed extents are re-serialized metadata, so unmodeled reserved bytes are emitted as
/// canonical zeros and this record never claims byte identity with the pre-write sectors.</para>
/// <para>这三项等价性分别上报，因为它们回答的是不同问题，且任一项都不能推出其他项。<see cref="BytesEquivalent"/>
/// 表示每个已编程字节回读都完全一致；<see cref="StructureEquivalent"/> 表示写入后从设备重新解析的分区表，其元数据
/// 摘要、块大小与块数都与所写入的快照一致；<see cref="CapacityEquivalent"/> 表示表声明的容量仍等于该区域上报的
/// 字节数。编程的范围是重新序列化的元数据，因此模型未携带的保留字节会以规范零值输出，本记录绝不声称与写入前的
/// 扇区逐字节相同。</para>
/// </remarks>
/// <param name="ExtentsProgrammed">The number of programmed metadata extents.
/// <para>已编程的元数据范围数量。</para></param>
/// <param name="BytesProgrammed">The total bytes submitted to the agent.
/// <para>已提交给代理的字节总数。</para></param>
/// <param name="BytesCompared">The total bytes read back and compared after the write.
/// <para>写入后已回读并比对的字节总数。</para></param>
/// <param name="BytesEquivalent">Whether every programmed byte matched its readback.
/// <para>每个已编程字节是否都与回读结果一致。</para></param>
/// <param name="StructureEquivalent">Whether the re-read table matches the written snapshot structurally.
/// <para>重新读取的分区表是否在结构上与所写入的快照一致。</para></param>
/// <param name="CapacityEquivalent">Whether the table capacity still equals the reported region bytes.
/// <para>分区表容量是否仍等于上报的区域字节数。</para></param>
/// <param name="WrittenMetadataSha256">The fingerprint of the table that was programmed.
/// <para>被编程的分区表摘要。</para></param>
/// <param name="ReadbackMetadataSha256">The fingerprint of the table re-read after the write.
/// <para>写入后重新读取的分区表摘要。</para></param>
public sealed record MtkGptWriteResult(int ExtentsProgrammed, long BytesProgrammed, long BytesCompared,
    bool BytesEquivalent, bool StructureEquivalent, bool CapacityEquivalent, string WrittenMetadataSha256,
    string ReadbackMetadataSha256);