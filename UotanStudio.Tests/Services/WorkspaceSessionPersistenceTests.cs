using System.Text.Json;
using UotanStudio.Pipeline.Projects;
using UotanStudio.Services;
using UotanStudio.Services.Ui.Workspace;
using UotanStudio.Services.Ui.Workspace.Documents;
using Xunit;

namespace UotanStudio.Tests.Services;

[Collection("Workspace panel state")]
public class WorkspaceSessionPersistenceTests
{
    [Fact]
    public void Descriptors_ForLogQcnAndMirror_ArePersistedButNotBulkClosed()
    {
        var log = DocumentOpenRegistry.ByKind("Log");
        var qcn = DocumentOpenRegistry.ByKind("Qcn");
        var mirror = DocumentOpenRegistry.ByKind("ScreenMirror");

        Assert.NotNull(log);
        Assert.NotNull(qcn);
        Assert.NotNull(mirror);
        Assert.False(log.IsDocument);
        Assert.False(qcn.IsDocument);
        Assert.False(mirror.IsDocument);
        Assert.True(log.PersistInSession);
        Assert.True(qcn.PersistInSession);
        Assert.True(mirror.PersistInSession);
        Assert.NotNull(log.Open);
        Assert.NotNull(log.GetPath);
        Assert.NotNull(qcn.Open);
        Assert.NotNull(qcn.GetPath);
        Assert.NotNull(mirror.Open);
        Assert.NotNull(mirror.GetPath);
        Assert.True(mirror.PathIsIdentifier);
        Assert.True(mirror.AllowEmptyPath);
    }

    [Fact]
    public void Request_Activate_DefaultsToTrueForUserDrivenOpens()
    {
        var request = new DocumentOpenRequest { Path = null };

        Assert.True(request.Activate);
    }

    [Fact]
    public void RestoreSelection_WithoutMatchingProject_KeepsSessionOnlyWindows()
    {
        List<OpenDocumentDto> saved =
        [
            new() { Kind = "Text", RelativePath = "source.txt" },
            new() { Kind = "Log", RelativePath = "logs/session.txt" },
            new() { Kind = "ScreenMirror", RelativePath = "ABC123" }
        ];

        var selected = WorkspaceAutoSaveService.SelectDocumentsForRestore(saved, null, "C:/projects/demo.uotan", false);

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Count);
        Assert.All(selected, d => Assert.False(DocumentOpenRegistry.ByKind(d.Kind)!.IsDocument));
    }

    [Fact]
    public void RestoreSelection_WithMatchingProject_RestoresEveryWindow()
    {
        List<OpenDocumentDto> saved =
        [
            new() { Kind = "Text", RelativePath = "source.txt" },
            new() { Kind = "Log", RelativePath = "logs/session.txt" }
        ];

        var selected = WorkspaceAutoSaveService.SelectDocumentsForRestore(saved, "C:/projects/demo.uotan", "C:/projects/demo.uotan", false);

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void RestoreSelection_WithStartupDocument_SkipsSessionRestore()
    {
        List<OpenDocumentDto> saved =
        [
            new() { Kind = "Log", RelativePath = "logs/session.txt" }
        ];

        var selected = WorkspaceAutoSaveService.SelectDocumentsForRestore(saved, "C:/projects/demo.uotan", "C:/projects/demo.uotan", true);

        Assert.Null(selected);
    }

    [Fact]
    public void ActiveWindow_PrefersActiveDocumentIdentity()
    {
        var documents = new[]
        {
            new OpenDocumentDto { Kind = "ScreenMirror", RelativePath = "ABC123" },
            new OpenDocumentDto { Kind = "Log", RelativePath = "logs/session.txt", IsActive = true }
        };

        var active = WorkspaceSessionCapture.ActiveWindow(null, documents);

        Assert.NotNull(active);
        Assert.Equal("Log", active.Kind);
        Assert.Equal("logs/session.txt", active.Identity);
        Assert.Null(active.DockId);
    }

    [Fact]
    public void SessionSnapshot_WithActiveWindow_RoundTripsThroughSourceGeneratedJson()
    {
        var snapshot = new WorkspaceSessionSnapshot
        {
            Documents =
            [
                new OpenDocumentDto { Kind = "Log", RelativePath = "logs/session.txt", IsActive = true },
                new OpenDocumentDto { Kind = "ScreenMirror", RelativePath = "ABC123" }
            ],
            ActiveWindow = new WorkspaceActiveWindow
            {
                Kind = "ScreenMirror",
                Identity = "ABC123"
            }
        };

        var json = JsonSerializer.Serialize(snapshot, AppSettingsJsonContext.Default.WorkspaceSessionSnapshot);
        var restored = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.WorkspaceSessionSnapshot);

        Assert.NotNull(restored);
        Assert.NotNull(restored.ActiveWindow);
        Assert.Equal("ScreenMirror", restored.ActiveWindow.Kind);
        Assert.Equal("ABC123", restored.ActiveWindow.Identity);
        Assert.Null(restored.ActiveWindow.DockId);
        Assert.Equal(2, restored.Documents.Count);
        Assert.Equal("logs/session.txt", restored.Documents[0].RelativePath);
    }
}