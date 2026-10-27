using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Diag;

/// <summary>
/// Diagnostic (DIAG) wire constants for the classic DIAG over serial/USB path: the NV read/write
/// commands, the subsystem-dispatch envelope and the EFS2 subsystem opcodes used for EFS file
/// operations.
/// <para>经典 DIAG（串口/USB 诊断口）协议的线上常量：NV 读写命令号、子系统分发包络与用于
/// EFS 文件操作的 EFS2 子系统操作码。</para>
/// </summary>
public static class DiagConstants
{
    /// <summary>
    /// DIAG NV read request command. <para>DIAG NV 读取请求命令号。</para>
    /// </summary>
    public const byte NvReadRequest = 0x26;

    /// <summary>
    /// DIAG NV read response command. <para>DIAG NV 读取响应命令号。</para>
    /// </summary>
    public const byte NvReadResponse = 0x27;

    /// <summary>
    /// DIAG NV write request command. <para>DIAG NV 写入请求命令号。</para>
    /// </summary>
    public const byte NvWriteRequest = 0x20;

    /// <summary>
    /// DIAG NV write response command. <para>DIAG NV 写入响应命令号。</para>
    /// </summary>
    public const byte NvWriteResponse = 0x21;

    /// <summary>
    /// DIAG subsystem dispatch request command. <para>DIAG 子系统分发请求命令号。</para>
    /// </summary>
    public const byte SubsysDispatchRequest = 0x4B;

    /// <summary>
    /// DIAG subsystem dispatch response command. <para>DIAG 子系统分发响应命令号。</para>
    /// </summary>
    public const byte SubsysDispatchResponse = 0x4C;

    /// <summary>
    /// The EFS2 diagnostics subsystem identifier inside a subsys dispatch envelope.
    /// <para>子系统分发包络中的 EFS2 诊断子系统标识。</para>
    /// </summary>
    public const ushort Efs2SubsystemId = 13;

    /// <summary>EFS2 op: peek a fixed-size region of EFS state. <para>EFS2 操作：读取 EFS 状态摘要。</para></summary>
    public const ushort Efs2Peek = 0;

    /// <summary>EFS2 op: stat a file by path. <para>EFS2 操作：按路径查询文件状态。</para></summary>
    public const ushort Efs2StatFile = 2;

    /// <summary>EFS2 op: stat a directory by path. <para>EFS2 操作：按路径查询目录状态。</para></summary>
    public const ushort Efs2StatDirectory = 3;

    /// <summary>EFS2 op: put a file (create or truncate). <para>EFS2 操作：写入文件（创建或截断）。</para></summary>
    public const ushort Efs2PutFile = 4;

    /// <summary>EFS2 op: get a file. <para>EFS2 操作：读取文件。</para></summary>
    public const ushort Efs2GetFile = 5;

    /// <summary>EFS2 op: list a directory. <para>EFS2 操作：列目录。</para></summary>
    public const ushort Efs2ListDirectory = 6;

    /// <summary>EFS2 op: delete a file. <para>EFS2 操作：删除文件。</para></summary>
    public const ushort Efs2DeleteFile = 8;

    /// <summary>EFS2 op: make a directory. <para>EFS2 操作：创建目录。</para></summary>
    public const ushort Efs2MakeDirectory = 9;

    /// <summary>EFS2 op: remove a directory. <para>EFS2 操作：删除目录。</para></summary>
    public const ushort Efs2RemoveDirectory = 10;

    /// <summary>EFS2 op: read the EFS sync state. <para>EFS2 操作：读取 EFS 同步状态。</para></summary>
    public const ushort Efs2SyncStatus = 14;

    /// <summary>EFS2 op: read an NV item by id through the EFS2 shim. <para>EFS2 操作：经 EFS2 垫片读取 NV 条目。</para></summary>
    public const ushort Efs2NvRead = 16;

    /// <summary>EFS2 op: write an NV item by id through the EFS2 shim. <para>EFS2 操作：经 EFS2 垫片写入 NV 条目。</para></summary>
    public const ushort Efs2NvWrite = 17;

    /// <summary>EFS2 op: factory reset the EFS. <para>EFS2 操作：恢复出厂 EFS。</para></summary>
    public const ushort Efs2FactoryReset = 20;

    /// <summary>
    /// NV item id of the six-digit Service Programming Code (SPC).
    /// <para>六位服务编程码（SPC）的 NV 条目号。</para>
    /// </summary>
    public const ushort NvSpcItemId = 0x1177;

    /// <summary>
    /// Maximum NV data payload DIAG carries for one item (classic NV items are 128 bytes).
    /// <para>DIAG 单条 NV 条目承载的数据上限（经典 NV 条目为 128 字节）。</para>
    /// </summary>
    public const int MaxNvDataBytes = 128;

    /// <summary>
    /// Maximum EFS path length accepted on the wire, including the terminating NUL.
    /// <para>线上接受的 EFS 路径长度上限，含结尾 NUL。</para>
    /// </summary>
    public const int MaxEfsPathBytes = 256;

    /// <summary>
    /// Maximum EFS file chunk the classic protocol transfers in one request.
    /// <para>经典协议单次请求传输的 EFS 文件块上限。</para>
    /// </summary>
    public const int MaxEfsChunkBytes = 1024;

    /// <summary>
    /// Default response timeout in milliseconds.
    /// <para>默认响应超时（毫秒）。</para>
    /// </summary>
    public const int DefaultTimeoutMs = 5000;

    /// <summary>
    /// Writes the 16-bit little-endian NV error code the NV responses carry; success is zero.
    /// <para>NV 响应携带的 16 位小端错误码；0 表示成功。</para>
    /// </summary>
    public static ushort ReadNvError(ReadOnlySpan<byte> payload) => payload.Length >= 1
        ? BinaryPrimitives.ReadUInt16LittleEndian(payload)
        : throw new ArgumentException("The NV response payload is too short.", nameof(payload));
}