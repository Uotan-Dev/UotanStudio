using Avalonia.Threading;
using Dock.Model.Core;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Workspace;

public sealed record WorkflowDocumentSnapshot(object Handle, string Id, string Path, string Kind,
    string? Text, bool CanEdit);

public interface IWorkflowDocuments
{
    Task<WorkflowDocumentSnapshot> ResolveAsync(string selector, CancellationToken cancellationToken);
    Task<WorkflowDocumentSnapshot> ReadAsync(WorkflowDocumentSnapshot snapshot, CancellationToken cancellationToken);
    Task<WorkflowDocumentSnapshot> ReplaceAsync(WorkflowDocumentSnapshot snapshot, string text,
        CancellationToken cancellationToken);
}

internal sealed class WorkspaceWorkflowDocuments(WorkspaceViewModel workspace) : IWorkflowDocuments
{
    public async Task<WorkflowDocumentSnapshot> ReadAsync(WorkflowDocumentSnapshot snapshot, CancellationToken cancellationToken)
        => await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Snapshot(RequireOpen(snapshot));
        });

    public async Task<WorkflowDocumentSnapshot> ResolveAsync(string selector, CancellationToken cancellationToken)
        => await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dock = workspace.FindDocumentsPanePublic();
            var documents = dock?.VisibleDockables?.ToArray() ?? [];
            var matches = string.IsNullOrWhiteSpace(selector)
                ? documents.Where(document => ReferenceEquals(document, dock?.ActiveDockable)).ToArray()
                : documents.Where(document => document.Id == selector
                    || document is ISearchableDocument searchable
                    && string.Equals(searchable.FilePath, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                throw Error("Node_Document_NotFound");
            }
            return Snapshot(matches[0]);
        });

    public async Task<WorkflowDocumentSnapshot> ReplaceAsync(WorkflowDocumentSnapshot snapshot, string text,
        CancellationToken cancellationToken)
        => await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = RequireOpen(snapshot);
            if (document is not ISearchableDocument { SupportsSearchReplacement: true } searchable
                || searchable.Editor?.IsReadOnly == true)
            {
                throw Error("Node_Document_ReadOnly");
            }
            if (snapshot.Text is null || searchable.SearchText != snapshot.Text
                || searchable.Editor is { } currentEditor && currentEditor.Text != snapshot.Text)
            {
                throw Error("Node_Document_Changed");
            }
            if (text.Length > TextSearchService.MaximumTextLength)
            {
                throw Error("Node_Document_TooLarge");
            }
            if (searchable.Editor is { } editor)
            {
                using (editor.Document.RunUpdate())
                {
                    editor.Document.Replace(0, editor.Document.TextLength, text);
                }
            }
            searchable.ApplySearchText(text);
            return Snapshot(document);
        });

    private static WorkflowDocumentSnapshot Snapshot(IDockable document)
    {
        var searchable = document as ISearchableDocument;
        var text = searchable?.SearchText;
        if (text?.Length > TextSearchService.MaximumTextLength)
        {
            throw Error("Node_Document_TooLarge");
        }
        return new(document, document.Id ?? string.Empty, searchable?.FilePath ?? string.Empty,
            document.GetType().Name, text,
            searchable?.SupportsSearchReplacement == true && searchable.Editor?.IsReadOnly != true);
    }

    private IDockable RequireOpen(WorkflowDocumentSnapshot snapshot)
    {
        if (snapshot.Handle is not IDockable document
            || workspace.FindDocumentsPanePublic()?.VisibleDockables?.Contains(document) != true)
        {
            throw Error("Node_Document_Closed");
        }
        return document;
    }

    private static InvalidOperationException Error(string key) => new(LanguageService.Instance.GetString(key));
}