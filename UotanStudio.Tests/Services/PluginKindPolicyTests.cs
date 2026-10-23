using UotanStudio.Projects;
using UotanStudio.Services.Plugins;
using UotanStudio.Workflow.Execution;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Pins the host-side plugin-kind policy: data kinds stay usable on every publish form, and any kind
/// unknown to the host fails closed as code-dependent.
/// <para>固化宿主侧插件种类策略：数据种类在任何发布形态都可用，宿主未知的种类一律保守视为依赖代码。</para>
/// </summary>
public sealed class PluginKindPolicyTests
{
    [Theory]
    [InlineData(VisualPluginKind.WorkflowTemplate)]
    [InlineData(VisualPluginKind.CompositeNode)]
    [InlineData(VisualPluginKind.ScriptNode)]
    public void RequiresDynamicCode_DataKind_ReturnsFalse(VisualPluginKind kind)
    {
        Assert.False(PluginKindPolicy.RequiresDynamicCode(kind));
    }

    [Fact]
    public void RequiresDynamicCode_UnknownKind_FailsClosed()
    {
        Assert.True(PluginKindPolicy.RequiresDynamicCode((VisualPluginKind)255));
    }

    [Fact]
    public void IsActivationBlocked_DataKindUnderAot_ReturnsFalse()
    {
        Assert.False(PluginKindPolicy.IsActivationBlocked(
            VisualPluginKind.CompositeNode, PluginRuntimeCapability.None));
    }

    [Fact]
    public void IsActivationBlocked_UnknownKindUnderAot_ReturnsTrue()
    {
        Assert.True(PluginKindPolicy.IsActivationBlocked(
            (VisualPluginKind)255, PluginRuntimeCapability.None));
    }

    [Fact]
    public void IsActivationBlocked_UnknownKindUnderJit_ReturnsFalse()
    {
        Assert.False(PluginKindPolicy.IsActivationBlocked(
            (VisualPluginKind)255, PluginRuntimeCapability.CodeLoading));
    }

    [Fact]
    public void BlockedKinds_EveryKnownKindUnderAot_IsEmpty()
    {
        Assert.Empty(PluginKindPolicy.BlockedKinds(PluginRuntimeCapability.None));
    }

    [Fact]
    public void BlockedKinds_UnderJit_IsEmpty()
    {
        Assert.Empty(PluginKindPolicy.BlockedKinds(PluginRuntimeCapability.CodeLoading));
    }
}