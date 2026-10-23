using Avalonia.Controls;
using Avalonia.Controls.Templates;
using UotanStudio.Views.Nodes;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// The pending-connection template cache that keeps one visual alive across view re-hosting.
/// <para>在视图重新挂载时保持唯一待处理连线控件的模板缓存。</para>
/// </summary>
public sealed class CachedPendingConnectionTemplateTests
{
    [Fact]
    public void Build_CalledTwice_ReturnsSameVisualAndBuildsOnce()
    {
        var inner = new CountingTemplate();
        var template = new NodeEditorView.CachedPendingConnectionTemplate(inner);

        var first = template.Build(new object());
        var second = template.Build(new object());

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, inner.BuildCount);
    }

    [Fact]
    public void Build_NullData_ReturnsNullWithoutBuilding()
    {
        var inner = new CountingTemplate();
        var template = new NodeEditorView.CachedPendingConnectionTemplate(inner);

        var visual = template.Build(null);

        Assert.Null(visual);
        Assert.Equal(0, inner.BuildCount);
    }

    [Fact]
    public void Match_DelegatesToInnerTemplate()
    {
        var inner = new CountingTemplate { MatchResult = true };
        var template = new NodeEditorView.CachedPendingConnectionTemplate(inner);

        Assert.True(template.Match(new object()));
        Assert.Equal(1, inner.MatchCount);
    }

    private sealed class CountingTemplate : IDataTemplate
    {
        public int BuildCount { get; private set; }

        public int MatchCount { get; private set; }

        public bool MatchResult { get; init; }

        public Control? Build(object? param)
        {
            BuildCount++;
            return new Border();
        }

        public bool Match(object? data)
        {
            MatchCount++;
            return MatchResult;
        }
    }
}