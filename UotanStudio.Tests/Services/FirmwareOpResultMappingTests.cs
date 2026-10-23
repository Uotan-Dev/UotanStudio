using UotanStudio.Firmware.Application.Equivalence;
using Xunit;
using HostFirmwareOperationResult = UotanStudio.Services.Firmware.FirmwareOperationResult;
using LibraryFirmwareOperationResult = UotanStudio.Firmware.Core.FirmwareOpResult;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Guards the single library-result to host-result mapping: equivalence metadata must survive the hop, and a
/// missing or unparsable flag must stay unevaluated instead of becoming an equivalence the library never computed.
/// <para>守护唯一的库结果到宿主结果映射：等价元数据必须跨越这一跳；缺失或无法解析的标记必须保持不评估，
/// 绝不能变成库从未计算过的等价声明。</para>
/// </summary>
public sealed class FirmwareOperationResultMappingTests
{
    [Fact]
    public void From_LibraryResultWithEquivalenceFlags_PreservesEveryDimensionAndDetail()
    {
        var library = LibraryFirmwareOperationResult.Ok("EXT4 rebuilt", ["data"], new Dictionary<string, string>
        {
            [FilesystemEquivalenceReport.ByteEquivalenceKey] = "False",
            [FilesystemEquivalenceReport.StructureEquivalenceKey] = "True",
            [FilesystemEquivalenceReport.ContentEquivalenceKey] = "True",
            [FilesystemEquivalenceReport.CapacityEquivalenceKey] = "True",
            ["RebuiltSize"] = "4096",
        });

        var host = HostFirmwareOperationResult.From(library);

        Assert.True(host.Success);
        Assert.Equal("EXT4 rebuilt", host.Summary);
        Assert.Equal(["data"], host.Entries);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, host.Equivalence.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, host.Equivalence.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, host.Equivalence.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.Equivalent, host.Equivalence.CapacityEquivalence);
        Assert.Equal("4096", host.Equivalence.Details["RebuiltSize"]);
    }

    [Fact]
    public void From_LibraryResultWithoutEquivalenceFlags_ReportsEveryDimensionAsNotEvaluated()
    {
        var library = LibraryFirmwareOperationResult.Ok("built", null, new Dictionary<string, string>
        {
            ["CompressionProfile"] = "Lz4:9",
        });

        AssertDimensionsUnevaluated(HostFirmwareOperationResult.From(library));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("yes")]
    public void From_UnparsableEquivalenceFlag_ReportsNotEvaluatedInsteadOfEquivalent(string value)
    {
        var library = LibraryFirmwareOperationResult.Ok("built", null, new Dictionary<string, string>
        {
            [FilesystemEquivalenceReport.ByteEquivalenceKey] = value,
            [FilesystemEquivalenceReport.ContentEquivalenceKey] = value,
        });

        var host = HostFirmwareOperationResult.From(library);

        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, host.Equivalence.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, host.Equivalence.ContentEquivalence);
    }

    [Fact]
    public void From_FailedLibraryResult_PreservesFailureWithoutInventingEquivalence()
    {
        var host = HostFirmwareOperationResult.From(LibraryFirmwareOperationResult.Fail("unsupported directory"));

        Assert.False(host.Success);
        Assert.Equal("unsupported directory", host.Summary);
        Assert.Empty(host.Entries);
        Assert.Empty(host.Metadata);
        AssertDimensionsUnevaluated(host);
    }

    [Fact]
    public void From_FailedLibraryResultWithCapacityVerdict_PreservesTheNegativeVerdict()
    {
        var library = new LibraryFirmwareOperationResult
        {
            Success = false,
            Summary = "does not fit",
            Metadata = new Dictionary<string, string>
            {
                [FilesystemEquivalenceReport.CapacityEquivalenceKey] = "False",
            },
        };

        var host = HostFirmwareOperationResult.From(library);

        Assert.False(host.Success);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, host.Equivalence.CapacityEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, host.Equivalence.ByteEquivalence);
    }

    [Fact]
    public void HostResultBuiltDirectly_ReportsAllDimensionsAsNotEvaluated()
    {
        AssertDimensionsUnevaluated(new HostFirmwareOperationResult { Success = true, Summary = "fresh build" });
        AssertDimensionsUnevaluated(HostFirmwareOperationResult.Fail("rejected"));
    }

    [Fact]
    public void Equivalence_EvaluatesEveryDimensionIndependently()
    {
        var host = HostFirmwareOperationResult.From(LibraryFirmwareOperationResult.Ok("packed", null, new Dictionary<string, string>
        {
            [FilesystemEquivalenceReport.ByteEquivalenceKey] = "True",
            [FilesystemEquivalenceReport.StructureEquivalenceKey] = "False",
        }));

        Assert.Equal(FirmwareEquivalenceState.Equivalent, host.Equivalence.ByteEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEquivalent, host.Equivalence.StructureEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, host.Equivalence.ContentEquivalence);
        Assert.Equal(FirmwareEquivalenceState.NotEvaluated, host.Equivalence.CapacityEquivalence);
    }

    private static void AssertDimensionsUnevaluated(HostFirmwareOperationResult result)
        => Assert.Equal(
        [
            FirmwareEquivalenceState.NotEvaluated,
            FirmwareEquivalenceState.NotEvaluated,
            FirmwareEquivalenceState.NotEvaluated,
            FirmwareEquivalenceState.NotEvaluated,
        ],
        [
            result.Equivalence.ByteEquivalence,
            result.Equivalence.StructureEquivalence,
            result.Equivalence.ContentEquivalence,
            result.Equivalence.CapacityEquivalence,
        ]);
}