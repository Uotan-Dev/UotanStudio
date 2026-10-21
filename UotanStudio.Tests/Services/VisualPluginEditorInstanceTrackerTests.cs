using UotanStudio.Services.Projects;
using UotanStudio.Services.VisualPlugins;
using UotanStudio.Views.Shell;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class VisualPluginEditorInstanceTrackerTests
{
    [Fact]
    public void TryRegister_SecondInstanceForKey_IsRejected()
    {
        var tracker = new VisualPluginEditorInstanceTracker();
        var first = new object();
        var second = new object();

        Assert.True(tracker.TryRegister("registration:tests.one", first));
        Assert.False(tracker.TryRegister("registration:tests.one", second));
        Assert.True(tracker.TryGet("registration:tests.one", out var current));
        Assert.Same(first, current);
    }

    [Fact]
    public void Remove_WithDifferentInstance_KeepsRegisteredEntry()
    {
        var tracker = new VisualPluginEditorInstanceTracker();
        var owner = new object();
        tracker.TryRegister("registration:tests.one", owner);

        tracker.Remove("registration:tests.one", new object());
        Assert.True(tracker.TryGet("registration:tests.one", out var current));
        Assert.Same(owner, current);

        tracker.Remove("registration:tests.one", owner);
        Assert.False(tracker.TryGet("registration:tests.one", out _));
    }

    [Fact]
    public void GetInstanceKey_SameRegistrationId_ReturnsSameKey()
    {
        var package = new VisualPluginPackage
        {
            Manifest = new VisualPluginManifest { Id = "tests.one", Name = "One" }
        };
        var first = new VisualPluginRegistration("first.uotanplugin", package, true, []);
        var second = new VisualPluginRegistration("second.uotanplugin", package, true, []);

        Assert.Equal("registration:tests.one", VisualPluginEditorWindow.GetInstanceKey(first));
        Assert.Equal(VisualPluginEditorWindow.GetInstanceKey(first), VisualPluginEditorWindow.GetInstanceKey(second));
        Assert.NotEqual(VisualPluginEditorWindow.GetInstanceKey(first),
            VisualPluginEditorWindow.GetInstanceKey(package));
    }
}