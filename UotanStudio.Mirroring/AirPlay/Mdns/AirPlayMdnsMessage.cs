using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace UotanStudio.Mirroring.AirPlay.Mdns;

/// <summary>One question record of an mDNS query. <para>mDNS 查询中的一条问题记录。</para></summary>
/// <param name="Name">Fully qualified domain name. <para>完全限定域名。</para></param>
/// <param name="Type">Record type, for example <see cref="MdnsMessage.TypePtr"/>. <para>记录类型，例如 <see cref="MdnsMessage.TypePtr"/>。</para></param>
/// <param name="Class">Query class with the unicast-response bit removed. <para>去除单播应答位后的查询类别。</para></param>
internal sealed record MdnsQuestion(string Name, ushort Type, ushort Class);

/// <summary>One resource record of an mDNS message. <para>mDNS 消息中的一条资源记录。</para></summary>
/// <param name="Name">Owner name. <para>所有者名称。</para></param>
/// <param name="Type">Record type. <para>记录类型。</para></param>
/// <param name="Class">Record class with the cache-flush bit removed. <para>去除缓存刷新位后的记录类别。</para></param>
/// <param name="TimeToLive">Announced lifetime in seconds. <para>宣告的生存时间（秒）。</para></param>
/// <param name="Address">IPv4 address for an A record. <para>A 记录的 IPv4 地址。</para></param>
/// <param name="Target">Target name for PTR and SRV records. <para>PTR 与 SRV 记录的目标名称。</para></param>
/// <param name="Port">Port for an SRV record. <para>SRV 记录的端口。</para></param>
/// <param name="Text">key=value strings for a TXT record. <para>TXT 记录的 key=value 字符串。</para></param>
internal sealed record MdnsRecord(string Name, ushort Type, ushort Class, uint TimeToLive,
    IPAddress? Address = null, string? Target = null, int Port = 0, IReadOnlyList<string>? Text = null)
{
    /// <summary>Builds a PTR record pointing at a service instance. <para>构建指向服务实例的 PTR 记录。</para></summary>
    public static MdnsRecord Pointer(string name, string target, uint timeToLive = 4500) =>
        new(name, MdnsMessage.TypePtr, MdnsMessage.ClassInternet, timeToLive, Target: target);

    /// <summary>Builds an SRV record for a service instance. <para>构建服务实例的 SRV 记录。</para></summary>
    public static MdnsRecord ServiceRecord(string name, string target, int port, uint timeToLive = 120) =>
        new(name, MdnsMessage.TypeSrv, MdnsMessage.ClassInternet | MdnsMessage.CacheFlushBit, timeToLive,
            Target: target, Port: port);

    /// <summary>Builds a TXT record holding the announced key/value pairs. <para>构建持有广播键值对的 TXT 记录。</para></summary>
    public static MdnsRecord TextRecord(string name, IReadOnlyList<string> values, uint timeToLive = 4500) =>
        new(name, MdnsMessage.TypeTxt, MdnsMessage.ClassInternet | MdnsMessage.CacheFlushBit, timeToLive,
            Text: values);

    /// <summary>Builds an A record for the host name. <para>构建主机名的 A 记录。</para></summary>
    public static MdnsRecord AddressRecord(string name, IPAddress address, uint timeToLive = 120) =>
        new(name, MdnsMessage.TypeA, MdnsMessage.ClassInternet | MdnsMessage.CacheFlushBit, timeToLive,
            Address: address);

    /// <summary>Returns the value of one TXT key. <para>返回一个 TXT 键的值。</para></summary>
    /// <param name="key">Key to look up; comparison is case-insensitive. <para>要查找的键；比较不区分大小写。</para></param>
    /// <returns>The value without its key and separator, or <see langword="null"/> when absent.
    /// <para>去掉键与分隔符的值；不存在时为 <see langword="null"/>。</para></returns>
    public string? GetText(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Text is null)
        {
            return null;
        }

        foreach (var entry in Text)
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && string.Equals(entry[..separator], key, StringComparison.OrdinalIgnoreCase))
            {
                return entry[(separator + 1)..];
            }
        }

        return null;
    }
}

/// <summary>
/// Bounded mDNS packet codec: name compression, the four record types the AirPlay announcement uses, and
/// strict length checks so a malformed datagram from the network cannot drive allocation.
/// <para>有界的 mDNS 报文编解码：名称压缩、AirPlay 广播用到的四种记录类型，以及严格的长度校验，
/// 使来自网络的畸形数据报无法驱动内存分配。</para>
/// </summary>
internal sealed class MdnsMessage
{
    /// <summary>Record type of an IPv4 address. <para>IPv4 地址的记录类型。</para></summary>
    public const ushort TypeA = 1;

    /// <summary>Record type of a name pointer. <para>名称指针的记录类型。</para></summary>
    public const ushort TypePtr = 12;

    /// <summary>Record type of a text string list. <para>文本字符串列表的记录类型。</para></summary>
    public const ushort TypeTxt = 16;

    /// <summary>Record type of a service location. <para>服务位置的记录类型。</para></summary>
    public const ushort TypeSrv = 33;

    /// <summary>Query type matching every record of a name. <para>匹配某名称全部记录的查询类型。</para></summary>
    public const ushort TypeAny = 255;

    /// <summary>Internet class. <para>互联网类别。</para></summary>
    public const ushort ClassInternet = 1;

    /// <summary>Class bit marking a unique record that supersedes cached copies. <para>标记唯一记录、用于取代缓存副本的类别位。</para></summary>
    public const ushort CacheFlushBit = 0x8000;

    /// <summary>Upper bound for one mDNS datagram accepted from the network. <para>从网络接受单个 mDNS 数据报的上限。</para></summary>
    public const int MaximumPacketBytes = 9000;

    /// <summary>Upper bound for the record count of one message. <para>单条消息记录总数的上限。</para></summary>
    public const int MaximumRecordCount = 96;

    /// <summary>Upper bound for one encoded domain name. <para>单个编码域名的上限。</para></summary>
    public const int MaximumNameBytes = 255;

    private const int MaximumCompressionJumps = 16;

    private static readonly Encoding NameEncoding = Encoding.ASCII;

    private MdnsMessage(ushort identifier, ushort flags, IReadOnlyList<MdnsQuestion> questions,
        IReadOnlyList<MdnsRecord> answers)
    {
        Identifier = identifier;
        Flags = flags;
        Questions = questions;
        Answers = answers;
    }

    /// <summary>Transaction identifier of the message. <para>消息的事务标识。</para></summary>
    public ushort Identifier { get; }

    /// <summary>Raw flags word of the message header. <para>消息头部的原始标志字。</para></summary>
    public ushort Flags { get; }

    /// <summary>Question records carried by the message. <para>消息携带的问题记录。</para></summary>
    public IReadOnlyList<MdnsQuestion> Questions { get; }

    /// <summary>Answer records carried by the message. <para>消息携带的应答记录。</para></summary>
    public IReadOnlyList<MdnsRecord> Answers { get; }

    /// <summary>Builds a standard mDNS query for one name and record type. <para>为单个名称与记录类型构建标准 mDNS 查询。</para></summary>
    /// <param name="name">Fully qualified name; 1 to 255 encoded bytes. <para>完全限定名称；编码后 1 到 255 字节。</para></param>
    /// <param name="type">Record type to ask for. <para>要查询的记录类型。</para></param>
    /// <returns>The encoded query packet. <para>编码后的查询报文。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or too long.
    /// <para><paramref name="name"/> 为空或过长。</para></exception>
    public static byte[] BuildQuery(string name, ushort type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var buffer = new List<byte>(64);
        WriteHeader(buffer, 0, 0, 1, 0);
        WriteName(buffer, name);
        WriteUInt16(buffer, type);
        WriteUInt16(buffer, ClassInternet);
        return [.. buffer];
    }

    /// <summary>Builds an authoritative response carrying the given answer records. <para>构建携带给定应答记录的权威应答。</para></summary>
    /// <param name="answers">Answer records in transmission order. <para>按发送顺序排列的应答记录。</para></param>
    /// <returns>The encoded response packet. <para>编码后的应答报文。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="answers"/> exceeds <see cref="MaximumRecordCount"/>.
    /// <para><paramref name="answers"/> 超过 <see cref="MaximumRecordCount"/>。</para></exception>
    public static byte[] BuildResponse(IReadOnlyList<MdnsRecord> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count > MaximumRecordCount)
        {
            throw new ArgumentException(
                "Too many mDNS records to announce. 要广播的 mDNS 记录过多。",
                nameof(answers));
        }

        var buffer = new List<byte>(512);
        WriteHeader(buffer, 0, 0x8400, 0, (ushort)answers.Count);
        foreach (var record in answers)
        {
            WriteName(buffer, record.Name);
            WriteUInt16(buffer, record.Type);
            WriteUInt16(buffer, record.Class);
            WriteUInt32(buffer, record.TimeToLive);
            var data = BuildRecordData(record);
            WriteUInt16(buffer, (ushort)data.Count);
            buffer.AddRange(data);
        }

        return [.. buffer];
    }

    /// <summary>Decodes one datagram. <para>解码一个数据报。</para></summary>
    /// <param name="payload">Complete datagram bytes; more than <see cref="MaximumPacketBytes"/> is rejected.
    /// <para>完整的数据报字节；超过 <see cref="MaximumPacketBytes"/> 会被拒绝。</para></param>
    /// <returns>The decoded questions and answers. <para>解码得到的问题与应答。</para></returns>
    /// <exception cref="InvalidDataException">The packet is truncated, uses an unsupported record layout or
    /// exceeds a bound. <para>报文被截断、使用不支持的记录布局或超出上限。</para></exception>
    public static MdnsMessage Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12)
        {
            throw new InvalidDataException("The mDNS packet is shorter than its header.");
        }

        if (payload.Length > MaximumPacketBytes)
        {
            throw new InvalidDataException("The mDNS packet exceeds the accepted size.");
        }

        var identifier = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        int questionCount = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        int answerCount = BinaryPrimitives.ReadUInt16BigEndian(payload[6..]);
        int authorityCount = BinaryPrimitives.ReadUInt16BigEndian(payload[8..]);
        int additionalCount = BinaryPrimitives.ReadUInt16BigEndian(payload[10..]);
        if (questionCount + answerCount + authorityCount + additionalCount > MaximumRecordCount)
        {
            throw new InvalidDataException("The mDNS packet announces too many records.");
        }

        var offset = 12;
        var questions = new List<MdnsQuestion>(questionCount);
        for (var index = 0; index < questionCount; index++)
        {
            var name = ReadName(payload, ref offset);
            var type = ReadUInt16(payload, ref offset);
            var recordClass = ReadUInt16(payload, ref offset);
            questions.Add(new MdnsQuestion(name, type, (ushort)(recordClass & ~CacheFlushBit)));
        }

        var answers = new List<MdnsRecord>(answerCount + authorityCount + additionalCount);
        for (var index = 0; index < answerCount + authorityCount + additionalCount; index++)
        {
            answers.Add(ReadRecord(payload, ref offset));
        }

        return new MdnsMessage(identifier, flags, questions, answers);
    }

    private static MdnsRecord ReadRecord(ReadOnlySpan<byte> payload, ref int offset)
    {
        var name = ReadName(payload, ref offset);
        var type = ReadUInt16(payload, ref offset);
        var recordClass = ReadUInt16(payload, ref offset);
        var timeToLive = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
        offset += 4;
        var dataLength = ReadUInt16(payload, ref offset);
        if (offset + dataLength > payload.Length)
        {
            throw new InvalidDataException("An mDNS resource record exceeds the packet.");
        }

        var data = payload.Slice(offset, dataLength);
        var record = new MdnsRecord(name, type, (ushort)(recordClass & ~CacheFlushBit), timeToLive);
        switch (type)
        {
            case TypeA:
                if (dataLength != 4)
                {
                    throw new InvalidDataException("An A record must carry four address bytes.");
                }

                record = record with { Address = new IPAddress(data) };
                break;
            case TypePtr:
                var pointerOffset = offset;
                record = record with { Target = ReadName(payload, ref pointerOffset) };
                RequireInside(dataLength, offset, pointerOffset);
                break;
            case TypeSrv:
                if (dataLength < 7)
                {
                    throw new InvalidDataException("An SRV record is shorter than its fixed fields.");
                }

                var serviceOffset = offset + 6;
                var serviceTarget = ReadName(payload, ref serviceOffset);
                RequireInside(dataLength, offset, serviceOffset);
                record = record with
                {
                    Port = BinaryPrimitives.ReadUInt16BigEndian(data[4..]),
                    Target = serviceTarget,
                };
                break;
            case TypeTxt:
                var values = new List<string>();
                var cursor = 0;
                while (cursor < dataLength)
                {
                    int length = data[cursor];
                    cursor++;
                    if (cursor + length > dataLength)
                    {
                        throw new InvalidDataException("A TXT string exceeds its record.");
                    }

                    values.Add(NameEncoding.GetString(data.Slice(cursor, length)));
                    cursor += length;
                }

                record = record with { Text = values };
                break;
        }

        offset += dataLength;
        return record;
    }

    private static void RequireInside(int dataLength, int dataOffset, int nameEnd)
    {
        if (nameEnd - dataOffset > dataLength)
        {
            throw new InvalidDataException("A name inside a record runs past the record data.");
        }
    }


    private static string ReadName(ReadOnlySpan<byte> payload, ref int offset)
    {
        var names = new List<string>(4);
        var cursor = offset;
        var jumps = 0;
        while (true)
        {
            if (cursor >= payload.Length)
            {
                throw new InvalidDataException("A name runs past the packet.");
            }

            int length = payload[cursor];
            if (length == 0)
            {
                offset = cursor + 1;
                return string.Join('.', names);
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= payload.Length)
                {
                    throw new InvalidDataException("A name pointer is truncated.");
                }

                var target = ((length & 0x3F) << 8) | payload[cursor + 1];
                offset = cursor + 2;
                if (target >= payload.Length || ++jumps > MaximumCompressionJumps)
                {
                    throw new InvalidDataException("A name pointer leaves the packet.");
                }

                cursor = target;
                continue;
            }

            if ((length & 0xC0) != 0 || cursor + 1 + length > payload.Length)
            {
                throw new InvalidDataException("A name label exceeds the packet.");
            }

            names.Add(NameEncoding.GetString(payload.Slice(cursor + 1, length)));
            if (names.Count > 32)
            {
                throw new InvalidDataException("A name carries too many labels.");
            }

            cursor += 1 + length;
        }
    }

    private static List<byte> BuildRecordData(MdnsRecord record)
    {
        var data = new List<byte>(64);
        switch (record.Type)
        {
            case TypeA:
                if (record.Address is null || record.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    throw new ArgumentException("An A record needs an IPv4 address.", nameof(record));
                }

                data.AddRange(record.Address.GetAddressBytes());
                break;
            case TypePtr:
                if (string.IsNullOrEmpty(record.Target))
                {
                    throw new ArgumentException("A PTR record needs a target name.", nameof(record));
                }

                WriteName(data, record.Target);
                break;
            case TypeSrv:
                if (string.IsNullOrEmpty(record.Target) || record.Port is < 0 or > 65535)
                {
                    throw new ArgumentException("An SRV record needs a target name and port.", nameof(record));
                }

                WriteUInt16(data, 0);
                WriteUInt16(data, 0);
                WriteUInt16(data, (ushort)record.Port);
                WriteName(data, record.Target);
                break;
            case TypeTxt:
                foreach (var entry in record.Text ?? [])
                {
                    var bytes = NameEncoding.GetBytes(entry);
                    if (bytes.Length > 255)
                    {
                        throw new ArgumentException("A TXT string exceeds 255 bytes.", nameof(record));
                    }

                    data.Add((byte)bytes.Length);
                    data.AddRange(bytes);
                }

                if (data.Count == 0)
                {
                    data.Add(0);
                }

                break;
            default:
                throw new ArgumentException("The record type cannot be announced.", nameof(record));
        }

        return data;
    }

    private static void WriteHeader(List<byte> buffer, ushort identifier, ushort flags, ushort questions,
        ushort answers)
    {
        WriteUInt16(buffer, identifier);
        WriteUInt16(buffer, flags);
        WriteUInt16(buffer, questions);
        WriteUInt16(buffer, answers);
        WriteUInt16(buffer, 0);
        WriteUInt16(buffer, 0);
    }

    private static void WriteName(List<byte> buffer, string name)
    {
        if (name.Length is 0 or > MaximumNameBytes)
        {
            throw new ArgumentException("The name is empty or too long.", nameof(name));
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63)
            {
                throw new ArgumentException("A name label must contain 1 to 63 characters.", nameof(name));
            }

            var bytes = NameEncoding.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        buffer.Add(0);
    }

    private static void WriteUInt16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static void WriteUInt32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)(value >> 24));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (offset + 2 > payload.Length)
        {
            throw new InvalidDataException("An mDNS field runs past the packet.");
        }

        var value = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += 2;
        return value;
    }
}