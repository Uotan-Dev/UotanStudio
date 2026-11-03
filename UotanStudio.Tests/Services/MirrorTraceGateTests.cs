using UotanStudio.Views.Editors;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MirrorTraceGateTests
{
    [Fact]
    public void Write_WhileTheEnvironmentVariableIsUnset_FormatsNothingAndCreatesNoFile()
    {
        var value = Environment.GetEnvironmentVariable(MirrorTrace.EnvironmentVariable);
        Assert.True(string.IsNullOrWhiteSpace(value),
            $"{MirrorTrace.EnvironmentVariable} is set, so this test cannot assert the default-off guarantee.");
        Assert.False(MirrorTrace.Enabled);
        Assert.Null(MirrorTrace.FilePath);

        var path = Path.Combine(Path.GetTempPath(), "uotan-mirror-trace.log");
        var existedBefore = File.Exists(path);
        var lengthBefore = existedBefore ? new FileInfo(path).Length : 0L;

        MirrorTrace.Write($"coverage {0}->{1} host={new object():x8}");

        if (existedBefore)
        {
            Assert.Equal(lengthBefore, new FileInfo(path).Length);
        }
        else
        {
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void MaximumBytes_KeepsTheTraceBoundedBelowTheConfiguredCeiling()
    {
        Assert.Equal(4L * 1024 * 1024, MirrorTrace.MaximumBytes);
    }
}