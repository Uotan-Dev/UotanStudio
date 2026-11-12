using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>
/// Assertion coverage for the EDL information rows the UI displays: every stage that actually answered
/// contributes its rows with that stage attached, refused facts stay absent, and the display contract holds
/// without any connected target. The real-device variants of these loops run from the standalone device probe
/// project instead of this host.
/// <para>界面显示的 EDL 信息行的断言覆盖：实际应答过的每个阶段都贡献带该阶段标注的行，被拒绝的事实保持缺席，
/// 且显示契约在没有任何已连接目标的情况下成立。这些循环的真机变体改由独立的设备探针项目运行。</para>
/// </summary>
public sealed class EdlInfoFormatterTests
{
    [Fact]
    public void Format_EmptySnapshot_ProducesNoRows()
    {
        var rows = EdlInfoFormatter.Format(new EdlInfoSnapshot());

        Assert.Empty(rows);
    }

    [Fact]
    public void Format_SaharaSnapshot_ReportsHelloFactsUnderSaharaStage()
    {
        var snapshot = new EdlInfoSnapshot
        {
            Protocol = EdlProtocolState.Sahara,
            Sahara = new SaharaHandshakeInfo(2, 1, 1024, SaharaMode.ImageTxPending),
        };

        var rows = EdlInfoFormatter.Format(snapshot);

        Assert.Equal("Sahara", SingleRow(rows, EdlInfoFormatter.ProtocolKey).Value);
        Assert.Equal(EdlInfoStage.Unread, SingleRow(rows, EdlInfoFormatter.ProtocolKey).Stage);
        Assert.Equal("2", SingleRow(rows, EdlInfoFormatter.SaharaVersionKey).Value);
        Assert.Equal("1", SingleRow(rows, EdlInfoFormatter.SaharaMinVersionKey).Value);
        Assert.Equal("0x400", SingleRow(rows, EdlInfoFormatter.SaharaMaxCommandLengthKey).Value);
        Assert.Equal("ImageTxPending", SingleRow(rows, EdlInfoFormatter.SaharaModeKey).Value);
        Assert.All(rows.Where(row => row.Key != EdlInfoFormatter.ProtocolKey),
            row => Assert.Equal(EdlInfoStage.Sahara, row.Stage));
        Assert.DoesNotContain(rows, row => row.Stage == EdlInfoStage.Firehose);
    }

    [Fact]
    public void Format_FirehoseSnapshot_ReportsStorageFactsUnderFirehoseStage()
    {
        var configureAck = FirehoseResponseParser.Parse(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><data><response value=\"ACK\" rawmode=\"false\" " +
            "MaxPayloadSizeToTargetInBytes=\"1048576\" MaxPayloadSizeFromTargetInBytes=\"8192\" StorageType=\"UFS\"/></data>");
        var capabilities = FirehoseCapabilitySnapshot.FromConfigureAck(
            configureAck, new FirehoseConfiguration { SectorSize = 4096, StorageType = FirehoseStorageType.Ufs },
            geometry: null, requiresReconnect: false, DateTimeOffset.Parse("2026-11-12T00:00:00Z"));
        var snapshot = new EdlInfoSnapshot
        {
            Protocol = EdlProtocolState.Firehose,
            Capabilities = capabilities,
            Geometry = new FirehoseStorageGeometry(0, 4096, 30119936),
            Storage = new FirehoseStorageDetails
            {
                SerialNumber = "KM8V7001JA-B813",
                ManufacturerId = "0x1ce",
                FirmwareVersion = "610",
                PageSize = 4096,
            },
        };

        var rows = EdlInfoFormatter.Format(snapshot);

        Assert.Equal("Firehose", SingleRow(rows, EdlInfoFormatter.ProtocolKey).Value);
        Assert.Equal("UFS", SingleRow(rows, EdlInfoFormatter.StorageTypeKey).Value);
        Assert.Equal("4096 B", SingleRow(rows, EdlInfoFormatter.SectorSizeKey).Value);
        Assert.Equal("30119936", SingleRow(rows, EdlInfoFormatter.SectorCountKey).Value);
        Assert.Equal("KM8V7001JA-B813", SingleRow(rows, EdlInfoFormatter.StorageSerialKey).Value);
        Assert.Equal("0x1ce", SingleRow(rows, EdlInfoFormatter.StorageManufacturerKey).Value);
        Assert.Equal("610", SingleRow(rows, EdlInfoFormatter.StorageFirmwareKey).Value);
        Assert.Equal("4096 B", SingleRow(rows, EdlInfoFormatter.PageSizeKey).Value);
        Assert.DoesNotContain(rows, row => row.Stage == EdlInfoStage.Sahara);
        Assert.All(rows.Where(row => row.Key != EdlInfoFormatter.ProtocolKey),
            row => Assert.Equal(EdlInfoStage.Firehose, row.Stage));
    }

    [Fact]
    public void Format_MergedSnapshot_EveryRowCarriesItsStageAndANonEmptyValue()
    {
        var sahara = new EdlInfoSnapshot
        {
            Protocol = EdlProtocolState.Sahara,
            Sahara = new SaharaHandshakeInfo(2, 1, 1024, SaharaMode.ImageTxPending),
        };
        var firehose = new EdlInfoSnapshot
        {
            Protocol = EdlProtocolState.Firehose,
            Capabilities = FirehoseCapabilitySnapshot.FromConfigureAck(
                FirehoseResponseParser.Parse(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><data><response value=\"ACK\" rawmode=\"false\" " +
                    "StorageType=\"UFS\"/></data>"),
                new FirehoseConfiguration { SectorSize = 4096, StorageType = FirehoseStorageType.Ufs },
                geometry: null, requiresReconnect: false, DateTimeOffset.Parse("2026-11-12T00:00:00Z")),
            Geometry = new FirehoseStorageGeometry(0, 4096, 30119936),
        };

        var rows = EdlInfoFormatter.Format(EdlInfoCollector.Merge(sahara, firehose));

        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.Key == EdlInfoFormatter.ProtocolKey);
        Assert.Contains(rows, row => row.Key == EdlInfoFormatter.SaharaVersionKey && row.Stage == EdlInfoStage.Sahara);
        Assert.Contains(rows, row => row.Key == EdlInfoFormatter.StorageTypeKey && row.Stage == EdlInfoStage.Firehose);
        Assert.Contains(rows, row => row.Key == EdlInfoFormatter.SectorSizeKey && row.Stage == EdlInfoStage.Firehose);
        Assert.All(rows, row => Assert.NotEmpty(row.Value));
    }

    [Fact]
    public void Format_SlotLayouts_StateSingleAndDualDifferently()
    {
        var single = EdlInfoFormatter.Format(new EdlInfoSnapshot
        {
            Protocol = EdlProtocolState.Firehose,
            SlotLayout = EdlSlotLayout.Single,
        });
        var dual = EdlInfoFormatter.Format(new EdlInfoSnapshot
        {
            Protocol = EdlProtocolState.Firehose,
            SlotLayout = EdlSlotLayout.Dual,
            ActiveSlot = "b",
        });

        Assert.Equal("Single (A-only)", SingleRow(single, EdlInfoFormatter.SlotLayoutKey).Value);
        Assert.DoesNotContain(single, row => row.Key == EdlInfoFormatter.ActiveSlotKey);
        Assert.Equal("Dual (A/B)", SingleRow(dual, EdlInfoFormatter.SlotLayoutKey).Value);
        Assert.Equal("b", SingleRow(dual, EdlInfoFormatter.ActiveSlotKey).Value);
    }

    [Fact]
    public void Format_RefusedIdentityFacts_StayAbsentFromRows()
    {
        var snapshot = new EdlInfoSnapshot
        {
            Protocol = EdlProtocolState.Sahara,
            Identity = new SaharaDeviceIdentity(0x9806169E, 0x000E60E1, 0, string.Empty),
        };

        var rows = EdlInfoFormatter.Format(snapshot);

        Assert.Equal("2550535838", SingleRow(rows, EdlInfoFormatter.IdentitySerialKey).Value);
        SingleRow(rows, EdlInfoFormatter.IdentityHardwareKey);
        Assert.DoesNotContain(rows, row => row.Key == EdlInfoFormatter.IdentitySblVersionKey);
        Assert.DoesNotContain(rows, row => row.Key == EdlInfoFormatter.IdentityChipIdentifierKey);
        Assert.DoesNotContain(rows, row => row.Key == EdlInfoFormatter.IdentityOemKeyHashKey);
    }

    [Fact]
    public void Format_SerialFallbackSnapshot_ReportsThePortName()
    {
        var rows = EdlInfoFormatter.Format(new EdlInfoSnapshot
        {
            Protocol = EdlProtocolState.Sahara,
            SerialPortName = "COM7",
        });

        Assert.Equal("COM7", SingleRow(rows, EdlInfoFormatter.SerialPortKey).Value);
        Assert.Equal(EdlInfoStage.Unread, SingleRow(rows, EdlInfoFormatter.SerialPortKey).Stage);
    }

    /// <summary>
    /// Returns the single row that carries <paramref name="key"/>, failing when the key is absent or duplicated.
    /// <para>返回携带 <paramref name="key"/> 的唯一行；键缺失或重复时失败。</para>
    /// </summary>
    /// <param name="rows">The formatted rows. <para>已格式化的行。</para></param>
    /// <param name="key">The stable row key to look up. <para>要查找的稳定行键。</para></param>
    /// <returns>The single matching row. <para>唯一匹配的行。</para></returns>
    private static EdlInfoRow SingleRow(IReadOnlyList<EdlInfoRow> rows, string key)
    {
        var matching = rows.Where(row => row.Key == key).ToArray();
        return Assert.Single(matching);
    }
}