using UotanStudio.Firmware.BoundedContexts.Patching;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class InitRcReferenceTests
{
    [Fact]
    public void CheckServiceReferences_LocalExternalAndOnrestart_ResolvesNamedTargets()
    {
        const string text = "service first /first\n    onrestart restart --only-if-running second\n"
            + "service second /second\non boot\nstart first\nstop second\nenable external\nexec_start first\n";
        var result = InitRcPatch.CheckServiceReferences(text, "external\r\n", TestContext.Current.CancellationToken);
        Assert.True(result.ReferencesValid);
        Assert.Equal(5, result.References);
        Assert.Equal(["external", "first", "second"], result.Services);
    }

    [Fact]
    public void CheckServiceReferences_MissingDynamicMalformedAndDuplicate_ReportsSeparateEvidence()
    {
        const string text = "service a /a\nservice a /other\n    override\non boot\nstart missing\n"
            + "start ${target}\nrestart --wrong a\nstop a extra\nclass_start main\ninterface_start aidl/test\n";
        var result = InitRcPatch.CheckServiceReferences(text, "", TestContext.Current.CancellationToken);
        Assert.False(result.ReferencesValid);
        Assert.Equal(["missing"], result.MissingServices);
        Assert.Equal(["a"], result.AmbiguousServices);
        Assert.Equal(["start ${target}"], result.DynamicReferences);
        Assert.Equal(["restart --wrong a", "stop a extra"], result.InvalidReferences);
        Assert.Equal(4, result.References);
    }

    [Fact]
    public void CheckServiceReferences_ImportDoesNotDeclareService_LeavesReferenceMissing()
    {
        var report = InitRcPatch.CheckServiceReferences("import /vendor/etc/init/service.rc\non boot\nstart remote\n", "",
            TestContext.Current.CancellationToken);
        Assert.False(report.ReferencesValid);
        Assert.Equal(["remote"], report.MissingServices);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("start \"\"")]
    [InlineData("restart --bad target")]
    public void CheckServiceReferences_InvalidArityOrTarget_DoesNotPass(string command)
    {
        var report = InitRcPatch.CheckServiceReferences("on boot\n" + command, "", TestContext.Current.CancellationToken);
        Assert.False(report.ReferencesValid);
        Assert.Equal(command, Assert.Single(report.InvalidReferences));
    }

    [Fact]
    public void CheckServiceReferences_Cancelled_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => InitRcPatch.CheckServiceReferences("", "", cancellation.Token));
    }
}