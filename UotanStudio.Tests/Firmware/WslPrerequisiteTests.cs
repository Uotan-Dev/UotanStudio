using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks tool detection and literal arguments against an existing shell. <para>通过现有 shell 检查工具探测和字面参数传递。</para></summary>
public sealed class WslPrerequisiteTests
{
    /// <summary>A missing tool cannot shift another tool's cached result. <para>缺失工具不能使其他工具的缓存结果错位。</para></summary>
    [Fact]
    public void MissingToolDoesNotChangeAvailableToolResults()
    {
        if (!WslTestHelpers.IsAlpineAvailable()) Assert.Skip("The selected WSL distribution is unavailable.");
        var missing = "uotan-missing-tool-" + Guid.NewGuid().ToString("N");
        Assert.False(WslTestHelpers.IsToolAvailable(missing));
        Assert.True(WslTestHelpers.IsToolAvailable("sh"));
        Assert.False(WslTestHelpers.IsToolAvailable(missing));
    }

    /// <summary>Shell metacharacters remain literal and nonzero exits remain observable. <para>shell 特殊字符保持字面含义，非零退出状态可观察。</para></summary>
    [Fact]
    public void ScriptArgumentsAndExitStatusArePreserved()
    {
        if (!WslTestHelpers.IsAlpineAvailable()) Assert.Skip("The selected WSL distribution is unavailable.");
        const string expected = "space ' quote \" $(printf substituted) `printf substituted`";
        var result = WslTestHelpers.RunWsl($"printf '%s' {WslTestHelpers.Quote(expected)}\nexit 23");
        Assert.Equal(expected, result.Output);
        Assert.Equal(23, result.ExitCode);
    }
}