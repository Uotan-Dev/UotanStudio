using System.Text.RegularExpressions;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Shell;

public sealed class RegexGeneratorViewModelTests
{
    [Fact]
    public void Generate_EmptyInput_SetsStatusMessage()
    {
        var vm = new RegexGeneratorViewModel();
        vm.Generate();

        Assert.Empty(vm.Pattern);
        Assert.NotEmpty(vm.StatusMessage);
    }

    [Fact]
    public void Generate_TargetInSource_InfersPattern()
    {
        var vm = new RegexGeneratorViewModel
        {
            SourceText = "build.prop version=34\nro.build.version.sdk=34",
            FindContent = "34",
        };

        vm.Generate();

        Assert.NotEmpty(vm.Pattern);
        Assert.Matches(new Regex(vm.Pattern), vm.SourceText);
    }

    [Fact]
    public void Generate_TargetNotInSource_FallsBackToEscapedLiteral()
    {
        var vm = new RegexGeneratorViewModel
        {
            SourceText = "hello world",
            FindContent = "foo[bar]",
        };

        vm.Generate();

        Assert.Equal(Regex.Escape("foo[bar]"), vm.Pattern);
        Assert.NotEmpty(vm.StatusMessage);
    }

    [Fact]
    public void ViewModel_Properties_CanBeSetAndRead()
    {
        var vm = new RegexGeneratorViewModel
        {
            SourceText = "initial source",
            FindContent = "initial target",
            Pattern = "abc",
            StatusMessage = "ready",
        };

        Assert.Equal("initial source", vm.SourceText);
        Assert.Equal("initial target", vm.FindContent);
        Assert.Equal("abc", vm.Pattern);
        Assert.Equal("ready", vm.StatusMessage);
    }
}