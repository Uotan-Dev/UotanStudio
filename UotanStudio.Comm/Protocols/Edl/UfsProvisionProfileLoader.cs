using System.Collections.Frozen;
using System.Globalization;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Reads a UFS provisioning profile document from untrusted storage into a validated
/// <see cref="UfsProvisionProfile"/>.
/// <para>把不可信存储中的 UFS provisioning 配置文件读入经过校验的 <see cref="UfsProvisionProfile"/>。</para>
/// </summary>
/// <remarks>
/// <para>
/// The document is a flat list, one <c>&lt;ufs&gt;</c> element per command of the provisioning
/// sequence, which is the shape the bundle-side provisioning parser already accepts. Attribute names
/// follow the provisioning attribute table the reference loader parses, where each name binds one
/// descriptor field and logical units are indexed from 0 to 7. Names match case-insensitively, and
/// the legacy property spellings <c>WContextCapacities</c> and <c>LUNumToGrow</c> are aliases of the
/// wire names. An unknown attribute, a duplicated field, or a value outside the documented range
/// rejects the whole document before any command is framed, so a typo cannot silently provision a
/// zeroed descriptor.
/// </para>
/// <para>
/// 文件是一份扁平列表，provisioning 序列的每条命令对应一个 <c>&lt;ufs&gt;</c> 元素，这正是 bundle
/// 侧 provisioning 解析器已接受的形状。属性名沿用参考下载器解析的 provisioning 属性表：每个名称绑定
/// 一个描述符字段，逻辑单元索引为 0 至 7。名称按大小写不敏感匹配，旧式属性写法 <c>WContextCapacities</c>
/// 与 <c>LUNumToGrow</c> 是线上名称的别名。未知属性、重复字段或超出所述范围的值都会在任何命令成帧之前
/// 拒绝整份文件，因此拼写错误不会悄悄地把描述符配成 0。
/// </para>
/// </remarks>
/// <example>
/// A profile for an eight-LUN device that stages the configuration without committing it:
/// <code lang="xml">
/// &lt;data&gt;
///   &lt;ufs type="common" bNumberLU="8" bBootEnable="1" bDescrAccessEn="1"
///        bInitPowerMode="0" bHighPriorityLUN="1" bSecureRemovalType="0"
///        bInitActiveICCLevel="0" wPeriodicRTCUpdate="0" bConfigDescrLock="0" /&gt;
///   &lt;ufs type="body" LUNum="0" bLUEnable="1" bBootLunID="1" size_in_kb="4096"
///        bDataReliability="0" bLUWriteProtect="0" bMemoryType="1"
///        bLogicalBlockSize="0" bProvisioningType="1" wContextCapabilities="0" /&gt;
///   &lt;ufs type="epilogue" LUNtoGrow="0" commit="0" /&gt;
/// &lt;/data&gt;
/// </code>
/// </example>
public static class UfsProvisionProfileLoader
{
    private const string SlotField = "slot";
    private const string CommitField = "commit";

    private static readonly FrozenDictionary<string, string> CommonFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bNumberLU"] = nameof(UfsCommon.BNumberLU),
            ["bBootEnable"] = nameof(UfsCommon.BBootEnable),
            ["bDescrAccessEn"] = nameof(UfsCommon.BDescrAccessEn),
            ["bInitPowerMode"] = nameof(UfsCommon.BInitPowerMode),
            ["bHighPriorityLUN"] = nameof(UfsCommon.BHighPriorityLUN),
            ["bSecureRemovalType"] = nameof(UfsCommon.BSecureRemovalType),
            ["bInitActiveICCLevel"] = nameof(UfsCommon.BInitActiveICCLevel),
            ["wPeriodicRTCUpdate"] = nameof(UfsCommon.WPeriodicRTCUpdate),
            ["bConfigDescrLock"] = nameof(UfsCommon.BConfigDescrLock),
            [SlotField] = SlotField
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> BodyFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LUNum"] = nameof(UfsBody.LUNum),
            ["bLUEnable"] = nameof(UfsBody.BLUEnable),
            ["bBootLunID"] = nameof(UfsBody.BBootLUNID),
            ["size_in_kb"] = nameof(UfsBody.SizeInKb),
            ["bDataReliability"] = nameof(UfsBody.BDataReliability),
            ["bLUWriteProtect"] = nameof(UfsBody.BLUWriteProtect),
            ["bMemoryType"] = nameof(UfsBody.BMemoryType),
            ["bLogicalBlockSize"] = nameof(UfsBody.BLogicalBlockSize),
            ["bProvisioningType"] = nameof(UfsBody.BProvisioningType),
            ["wContextCapabilities"] = nameof(UfsBody.WContextCapacities),
            ["WContextCapacities"] = nameof(UfsBody.WContextCapacities),
            ["desc"] = nameof(UfsBody.Desc),
            [SlotField] = SlotField
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> EpilogueFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LUNtoGrow"] = nameof(UfsEpilogue.LUNumToGrow),
            ["LUNumToGrow"] = nameof(UfsEpilogue.LUNumToGrow),
            [CommitField] = CommitField,
            [SlotField] = SlotField
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads and validates a provisioning profile from a file path.
    /// <para>从文件路径读取并校验 provisioning 配置文件。</para>
    /// </summary>
    /// <param name="path">Path to the profile document. <para>配置文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The validated profile. <para>校验通过的配置文件。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.
    /// <para><paramref name="path"/> 为空或仅含空白。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.
    /// <para><paramref name="path"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The document is larger than <see cref="UfsProvisionProfile.MaximumProfileBytes"/> bytes, is not well-formed, or violates a documented bound.
    /// <para>文件大于 <see cref="UfsProvisionProfile.MaximumProfileBytes"/> 字节、格式不合法，或违反所述上限。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public static async Task<UfsProvisionProfile> LoadAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var information = new FileInfo(path);
        if (!information.Exists)
        {
            throw new FileNotFoundException($"The UFS provisioning profile was not found: {path}", path);
        }

        if (information.Length > UfsProvisionProfile.MaximumProfileBytes)
        {
            throw new InvalidDataException(
                $"The UFS provisioning profile is {information.Length} bytes, above the {UfsProvisionProfile.MaximumProfileBytes}-byte input limit.");
        }

        using var bounded = new BoundedProfileStream(
            File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            UfsProvisionProfile.MaximumProfileBytes,
            leaveOpen: false);
        return await LoadCoreAsync(bounded, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and validates a provisioning profile from a stream, consuming at most
    /// <see cref="UfsProvisionProfile.MaximumProfileBytes"/> bytes.
    /// <para>从流中读取并校验 provisioning 配置文件，最多消费 <see cref="UfsProvisionProfile.MaximumProfileBytes"/> 字节。</para>
    /// </summary>
    /// <param name="stream">The document stream; it stays open. <para>文件流；保持打开。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The validated profile. <para>校验通过的配置文件。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="stream"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The stream yields more than the input limit, is not well-formed, or violates a documented bound.
    /// <para>流产出超过输入上限、格式不合法，或违反所述上限。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已触发。</para></exception>
    public static async Task<UfsProvisionProfile> LoadAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.CanSeek && stream.Length - stream.Position > UfsProvisionProfile.MaximumProfileBytes)
        {
            throw new InvalidDataException(
                $"The UFS provisioning profile stream holds {stream.Length - stream.Position} bytes, above the {UfsProvisionProfile.MaximumProfileBytes}-byte input limit.");
        }

        using var bounded = new BoundedProfileStream(stream, UfsProvisionProfile.MaximumProfileBytes,
            leaveOpen: true);
        return await LoadCoreAsync(bounded, "the supplied stream", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<UfsProvisionProfile> LoadCoreAsync(BoundedProfileStream stream, string origin,
        CancellationToken cancellationToken)
    {
        var document = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return FlashBundle.FlashBundleXml.Load(stream);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    $"The UFS provisioning profile in {origin} could not be read: {InnermostMessage(exception)}",
                    exception);
            }
        }, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return Convert(document, origin);
    }

    private static UfsProvisionProfile Convert(XDocument document, string origin)
    {
        if (document.Root is null)
        {
            throw new InvalidDataException($"The UFS provisioning profile in {origin} has no root element.");
        }

        if (document.Root.HasAttributes)
        {
            throw new InvalidDataException(
                $"The root element of the UFS provisioning profile in {origin} carries attributes; only child elements are allowed.");
        }

        var elements = document.Root.Elements().ToArray();
        if (elements.Length == 0)
        {
            throw new InvalidDataException(
                $"The UFS provisioning profile in {origin} declares no commands; one common command, at least one LUN body and one epilogue are required.");
        }

        if (elements.Length > UfsProvisionProfile.MaximumLunCount + 2)
        {
            throw new InvalidDataException(
                $"The UFS provisioning profile in {origin} declares {elements.Length} commands, above the {UfsProvisionProfile.MaximumLunCount + 2}-command limit.");
        }

        var entries = new List<(string Type, Dictionary<string, string> Values)>(elements.Length);
        foreach (var element in elements)
        {
            if (!string.Equals(element.Name.LocalName, "ufs", StringComparison.OrdinalIgnoreCase)
                || element.Name.NamespaceName.Length > 0)
            {
                throw new InvalidDataException(
                    $"The UFS provisioning profile in {origin} contains a <{element.Name.LocalName}> element; only unnamespaced ufs elements are accepted.");
            }

            var type = NormalizeType(element.Attribute("type")?.Value, origin);
            entries.Add((type, ReadAttributes(element, type, origin)));
        }

        if (entries[0].Type != "common" || entries[^1].Type != "epilogue"
            || entries.Skip(1).SkipLast(1).Any(entry => entry.Type != "body"))
        {
            throw new InvalidDataException(
                $"The UFS provisioning profile in {origin} must start with one common command, continue with one command per LUN, and end with one epilogue.");
        }

        var bodyCount = entries.Count(entry => entry.Type == "body");
        if (bodyCount == 0)
        {
            throw new InvalidDataException(
                $"The UFS provisioning profile in {origin} declares 0 LUN bodies; at least one is required.");
        }

        var common = ReadCommon(entries[0].Values, origin);
        var bodies = new List<UfsBody>(bodyCount);
        foreach (var entry in entries.Skip(1).SkipLast(1))
        {
            bodies.Add(ReadBody(entry.Values, origin));
        }

        RequireSessionSlot(entries, origin);
        var epilogueValues = entries[^1].Values;
        var epilogue = new UfsEpilogue
        {
            LUNumToGrow = ReadInteger(epilogueValues, nameof(UfsEpilogue.LUNumToGrow), 0, byte.MaxValue, origin, 0)
        };
        var commit = ReadInteger(epilogueValues, CommitField, 0, 1, origin, 0) == 1;

        return new UfsProvisionProfile(common, bodies, epilogue, commit);
    }

    private static string NormalizeType(string? type, string origin) => type?.Trim().ToLowerInvariant() switch
    {
        "common" => "common",
        "body" => "body",
        "epilogue" => "epilogue",
        _ => throw new InvalidDataException(
            $"A command in the UFS provisioning profile in {origin} has the unsupported type '{type}'.")
    };

    private static Dictionary<string, string> ReadAttributes(XElement element, string type, string origin)
    {
        var attributes = element.Attributes()
            .Where(attribute => !string.Equals(attribute.Name.LocalName, "type", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (attributes.Length == 0)
        {
            throw new InvalidDataException(
                $"The {type} command in the UFS provisioning profile in {origin} carries no descriptor attributes.");
        }

        if (attributes.Length > UfsProvisionProfile.MaximumDescriptorAttributes)
        {
            throw new InvalidDataException(
                $"The {type} command in the UFS provisioning profile in {origin} carries {attributes.Length} descriptor attributes, above the {UfsProvisionProfile.MaximumDescriptorAttributes}-attribute limit.");
        }

        var table = type switch
        {
            "common" => CommonFields,
            "body" => BodyFields,
            _ => EpilogueFields
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            if (attribute.Name.NamespaceName.Length > 0)
            {
                throw new InvalidDataException(
                    $"The attribute {attribute.Name.LocalName} in the UFS provisioning profile in {origin} is namespaced.");
            }

            if (!table.TryGetValue(attribute.Name.LocalName, out var field))
            {
                throw new InvalidDataException(
                    $"The attribute {attribute.Name.LocalName} is not a UFS provisioning descriptor field in {origin}.");
            }

            if (!values.TryAdd(field, attribute.Value))
            {
                throw new InvalidDataException(
                    $"The descriptor field {field} is supplied more than once by the {type} command in the UFS provisioning profile in {origin}.");
            }
        }

        return values;
    }

    private static UfsCommon ReadCommon(Dictionary<string, string> values, string origin) => new()
    {
        BNumberLU = ReadInteger(values, nameof(UfsCommon.BNumberLU), 0, byte.MaxValue, origin, 0),
        BBootEnable = ReadInteger(values, nameof(UfsCommon.BBootEnable), 0, byte.MaxValue, origin, 0),
        BDescrAccessEn = ReadInteger(values, nameof(UfsCommon.BDescrAccessEn), 0, byte.MaxValue, origin, 0),
        BInitPowerMode = ReadInteger(values, nameof(UfsCommon.BInitPowerMode), 0, byte.MaxValue, origin, 0),
        BHighPriorityLUN = ReadInteger(values, nameof(UfsCommon.BHighPriorityLUN), 0, byte.MaxValue, origin, 0),
        BSecureRemovalType = ReadInteger(values, nameof(UfsCommon.BSecureRemovalType), 0, byte.MaxValue, origin, 0),
        BInitActiveICCLevel = ReadInteger(values, nameof(UfsCommon.BInitActiveICCLevel), 0, byte.MaxValue, origin, 0),
        WPeriodicRTCUpdate = ReadInteger(values, nameof(UfsCommon.WPeriodicRTCUpdate), 0, ushort.MaxValue, origin, 0),
        BConfigDescrLock = ReadInteger(values, nameof(UfsCommon.BConfigDescrLock), 0, byte.MaxValue, origin, 0)
    };

    private static UfsBody ReadBody(Dictionary<string, string> values, string origin)
    {
        var description = values.TryGetValue(nameof(UfsBody.Desc), out var raw) ? raw : string.Empty;
        if (description.Length > UfsProvisionProfile.MaximumDescriptionLength)
        {
            throw new InvalidDataException(
                $"A LUN description in the UFS provisioning profile in {origin} is {description.Length} characters, above the {UfsProvisionProfile.MaximumDescriptionLength}-character limit.");
        }

        return new UfsBody
        {
            LUNum = ReadInteger(values, nameof(UfsBody.LUNum), 0, UfsProvisionProfile.MaximumLunCount - 1, origin, 0),
            BLUEnable = ReadInteger(values, nameof(UfsBody.BLUEnable), 0, byte.MaxValue, origin, 0),
            BBootLUNID = ReadInteger(values, nameof(UfsBody.BBootLUNID), 0, byte.MaxValue, origin, 0),
            SizeInKb = ReadInteger(values, nameof(UfsBody.SizeInKb), 0, UfsProvisionProfile.MaximumLunSizeInKb, origin, 0L),
            BDataReliability = ReadInteger(values, nameof(UfsBody.BDataReliability), 0, byte.MaxValue, origin, 0),
            BLUWriteProtect = ReadInteger(values, nameof(UfsBody.BLUWriteProtect), 0, byte.MaxValue, origin, 0),
            BMemoryType = ReadInteger(values, nameof(UfsBody.BMemoryType), 0, byte.MaxValue, origin, 0),
            BLogicalBlockSize = ReadInteger(values, nameof(UfsBody.BLogicalBlockSize), 0, byte.MaxValue, origin, 0),
            BProvisioningType = ReadInteger(values, nameof(UfsBody.BProvisioningType), 0, byte.MaxValue, origin, 0),
            WContextCapacities = ReadInteger(values, nameof(UfsBody.WContextCapacities), 0, ushort.MaxValue, origin, 0),
            Desc = description.Length == 0 ? null : description
        };
    }

    private static int ReadInteger(Dictionary<string, string> values, string field, int minimum, int maximum,
        string origin, int fallback)
        => (int)ReadInteger(values, field, minimum, maximum, origin, (long)fallback);

    private static long ReadInteger(Dictionary<string, string> values, string field, long minimum, long maximum,
        string origin, long fallback)
    {
        if (!values.TryGetValue(field, out var raw))
        {
            return fallback;
        }

        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > maximum)
        {
            throw new InvalidDataException(
                $"The descriptor field {field} in the UFS provisioning profile in {origin} must be a whole number from {minimum} to {maximum}.");
        }

        return parsed;
    }

    private static void RequireSessionSlot(
        IReadOnlyList<(string Type, Dictionary<string, string> Values)> entries, string origin)
    {
        foreach (var entry in entries)
        {
            if (entry.Values.ContainsKey(SlotField))
            {
                throw new InvalidDataException(
                    $"The UFS provisioning profile in {origin} pins a slot; a profile never selects one because the Firehose session slot is authoritative.");
            }
        }
    }

    private static string InnermostMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is { } inner)
        {
            current = inner;
        }

        return string.IsNullOrWhiteSpace(current.Message) ? "the document is malformed." : current.Message;
    }

    /// <summary>
    /// A read-only view that refuses to yield more than a fixed number of bytes, so a hostile profile
    /// cannot make the parser buffer an unbounded document.
    /// <para>只读视图，拒绝产出超过固定长度的字节，使恶意配置文件无法迫使解析器缓冲无上限的文件。</para>
    /// </summary>
    /// <param name="inner">The wrapped stream. <para>被包装的流。</para></param>
    /// <param name="maximumBytes">The hard read budget. <para>读取字节硬上限。</para></param>
    /// <param name="leaveOpen">Whether the wrapped stream survives this view's disposal. <para>视图释放时是否保留被包装的流。</para></param>
    private sealed class BoundedProfileStream(Stream inner, int maximumBytes, bool leaveOpen) : Stream
    {
        private long _consumed;

        /// <summary>
        /// Gets whether the view supports reading.
        /// <para>获取视图是否支持读取。</para>
        /// </summary>
        /// <value>Always <see langword="true"/>. <para>始终为 <see langword="true"/>。</para></value>
        public override bool CanRead => true;

        /// <summary>
        /// Gets whether the view supports seeking.
        /// <para>获取视图是否支持定位。</para>
        /// </summary>
        /// <value>Always <see langword="false"/>, because only a forward read budget is enforced.
        /// <para>始终为 <see langword="false"/>，因为只强制前向读取预算。</para></value>
        public override bool CanSeek => false;

        /// <summary>
        /// Gets whether the view supports writing.
        /// <para>获取视图是否支持写入。</para>
        /// </summary>
        /// <value>Always <see langword="false"/>. <para>始终为 <see langword="false"/>。</para></value>
        public override bool CanWrite => false;

        /// <summary>
        /// Gets the length of the stream.
        /// <para>获取流的长度。</para>
        /// </summary>
        /// <value>Never available. <para>永不可用。</para></value>
        /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
        public override long Length => throw new NotSupportedException();

        /// <summary>
        /// Gets or sets the position within the stream.
        /// <para>获取或设置流内的位置。</para>
        /// </summary>
        /// <value>The number of bytes consumed so far. <para>已消费的字节数。</para></value>
        /// <exception cref="NotSupportedException">Setting the position is not supported.
        /// <para>不支持设置位置。</para></exception>
        public override long Position
        {
            get => _consumed;
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Flushes the stream.
        /// <para>刷新流。</para>
        /// </summary>
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        /// <summary>
        /// Reads at most the remaining budget into the buffer.
        /// <para>向缓冲区读取至多剩余预算的字节。</para>
        /// </summary>
        /// <param name="buffer">The destination. <para>目标缓冲区。</para></param>
        /// <returns>The number of bytes read. <para>读取到的字节数。</para></returns>
        /// <exception cref="InvalidDataException">The budget was exhausted while the parser still needed bytes.
        /// <para>解析器仍需字节但预算已耗尽。</para></exception>
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..(int)Math.Min(Remaining, buffer.Length)]);
            _consumed += read;
            return read;
        }

        /// <summary>
        /// Reads at most the remaining budget into the buffer asynchronously.
        /// <para>异步向缓冲区读取至多剩余预算的字节。</para>
        /// </summary>
        /// <param name="buffer">The destination. <para>目标缓冲区。</para></param>
        /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
        /// <returns>The number of bytes read. <para>读取到的字节数。</para></returns>
        /// <exception cref="InvalidDataException">The budget was exhausted while the parser still needed bytes.
        /// <para>解析器仍需字节但预算已耗尽。</para></exception>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner
                .ReadAsync(buffer[..(int)Math.Min(Remaining, buffer.Length)], cancellationToken)
                .ConfigureAwait(false);
            _consumed += read;
            return read;
        }

        /// <summary>
        /// Reads one byte.
        /// <para>读取一个字节。</para>
        /// </summary>
        /// <returns>The byte value, or <c>-1</c> at the end of the stream.
        /// <para>字节值；流末尾为 <c>-1</c>。</para></returns>
        /// <exception cref="InvalidDataException">The budget was exhausted while the parser still needed bytes.
        /// <para>解析器仍需字节但预算已耗尽。</para></exception>
        public override int ReadByte()
        {
            var value = inner.ReadByte();
            if (value >= 0)
            {
                _consumed++;
            }

            return value;
        }

        /// <summary>
        /// Seeks within the stream.
        /// <para>在流内定位。</para>
        /// </summary>
        /// <param name="offset">The target offset. <para>目标偏移。</para></param>
        /// <param name="origin">The reference point. <para>参考点。</para></param>
        /// <returns>Never. <para>永不返回。</para></returns>
        /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>
        /// Sets the stream length.
        /// <para>设置流长度。</para>
        /// </summary>
        /// <param name="value">The requested length. <para>请求的长度。</para></param>
        /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// Writes to the stream.
        /// <para>向流写入。</para>
        /// </summary>
        /// <param name="buffer">The source bytes. <para>源字节。</para></param>
        /// <param name="offset">The source offset. <para>源偏移。</para></param>
        /// <param name="count">The byte count. <para>字节数。</para></param>
        /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>
        /// Releases the view and, unless it was asked to leave the stream open, the wrapped stream.
        /// <para>释放视图，并在未要求保留时释放被包装的流。</para>
        /// </summary>
        /// <param name="disposing">Whether managed resources are being released. <para>是否正在释放托管资源。</para></param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private long Remaining
        {
            get
            {
                var remaining = maximumBytes - _consumed;
                if (remaining <= 0)
                {
                    throw new InvalidDataException(
                        $"The UFS provisioning profile exceeds the {maximumBytes}-byte input limit.");
                }

                return remaining;
            }
        }
    }
}