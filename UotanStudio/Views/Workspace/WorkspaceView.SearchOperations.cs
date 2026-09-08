using System.ComponentModel;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Dock.Model.Core;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Workspace;
using UotanStudio.ViewModels.Editors;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Controls;

namespace UotanStudio.Views.Workspace;

public partial class WorkspaceView
{
    private sealed record SearchDocument(IDockable? Document, string? Path, string Text, int Start, int Length);
    private readonly List<(SearchDocument Document, TextSearchMatch Match)> _searchMatches = [];
    private readonly HashSet<INotifyPropertyChanged> _searchObserved = [];
    private CancellationTokenSource? _documentSearchCancellation;
    private IDockable? _searchContextDocument;
    private IDockable? _selectionDocument;
    private int _selectionStart, _selectionLength, _searchAnchor, _searchMatchIndex = -1;
    private bool _searchBusy, _searchActionRunning, _searchNavigating;
    private TextAnchor? _selectionStartAnchor, _selectionEndAnchor;
    private IDockable? _searchResumeDocument;
    private int? _searchResumeOffset;
    private string? _searchProjectRoot;
    private string? _searchOperationStatus;

    private IDockable? ActiveSearchDocument => DataContext is WorkspaceViewModel vm
        ? vm.FindDocumentsPanePublic()?.ActiveDockable : null;

    private static string? SearchText(IDockable? document) => document switch
    {
        FileViewModel { IsBinary: false } text => text.Text,
        DtbEditorViewModel dtb => dtb.Source,
        _ => null
    };

    private static string? SearchPath(IDockable? document) => document switch
    {
        FileViewModel text => text.FilePath,
        DtbEditorViewModel dtb => dtb.FilePath,
        _ => null
    };

    private static TextEditor? SearchEditor(IDockable? document)
    {
        var editor = document switch
        {
            FileViewModel text => text.Editor,
            DtbEditorViewModel dtb => dtb.Editor,
            _ => null
        };
        return editor is { IsLoaded: true } && ReferenceEquals(editor.DataContext, document) ? editor : null;
    }

    private void CaptureSearchSelection()
    {
        _searchContextDocument = _selectionDocument = ActiveSearchDocument;
        var editor = SearchEditor(_selectionDocument);
        var textLength = editor?.Document.TextLength ?? 0;
        _selectionStart = Math.Clamp(editor?.SelectionStart ?? 0, 0, textLength);
        _selectionLength = Math.Clamp(editor?.SelectionLength ?? 0, 0, textLength - _selectionStart);
        _searchAnchor = Math.Clamp(editor?.CaretOffset ?? 0, 0, textLength);
        _selectionStartAnchor = editor?.Document.CreateAnchor(_selectionStart);
        _selectionEndAnchor = editor?.Document.CreateAnchor(_selectionStart + _selectionLength);
        if (_selectionStartAnchor is not null)
        {
            _selectionStartAnchor.SurviveDeletion = true;
            _selectionStartAnchor.MovementType = AnchorMovementType.BeforeInsertion;
        }
        if (_selectionEndAnchor is not null)
        {
            _selectionEndAnchor.SurviveDeletion = true;
            _selectionEndAnchor.MovementType = AnchorMovementType.AfterInsertion;
        }
    }

    private void OnSearchDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Text" or "Source" && !_searchActionRunning) QueueDocumentSearch();
    }

    private void StopDocumentSearch()
    {
        _documentSearchCancellation?.Cancel();
        foreach (var document in _searchObserved) document.PropertyChanged -= OnSearchDocumentChanged;
        _searchObserved.Clear();
        _searchMatches.Clear();
        _searchBusy = false;
        _selectionStartAnchor = _selectionEndAnchor = null;
        _selectionDocument = _searchContextDocument = _searchResumeDocument = null;
        _searchResumeOffset = null;
    }

    private void QueueDocumentSearch()
    {
        if (!DocumentSearchPanel.IsVisible || _searchActionRunning) return;
        _documentSearchCancellation?.Cancel();
        _documentSearchCancellation?.Dispose();
        _documentSearchCancellation = new();
        _ = RefreshDocumentSearchAsync(_documentSearchCancellation.Token);
    }

    private async Task RefreshDocumentSearchAsync(CancellationToken cancellationToken)
    {
        _searchBusy = true;
        _searchMatchIndex = -1;
        DocumentSearchPanel.SetStatus(LanguageService.Instance.GetString("FindReplace_Searching"), false);
        try
        {
            await Task.Delay(180, cancellationToken);
            if (DataContext is not WorkspaceViewModel vm) return;
            var options = DocumentSearchPanel.Options;
            var scope = DocumentSearchPanel.Scope;
            var root = ProjectContextService.Instance.RootDirectory;
            _searchProjectRoot = root;
            if (_selectionStartAnchor is { IsDeleted: false } startAnchor && _selectionEndAnchor is { IsDeleted: false } endAnchor)
            {
                _selectionStart = startAnchor.Offset;
                _selectionLength = Math.Max(0, endAnchor.Offset - startAnchor.Offset);
            }
            var open = WorkspaceSessionCapture.Enumerate(vm.Layout).Where(d => SearchText(d) is not null).ToArray();
            foreach (var observed in _searchObserved) observed.PropertyChanged -= OnSearchDocumentChanged;
            _searchObserved.Clear();
            foreach (var observed in open.OfType<INotifyPropertyChanged>())
                if (_searchObserved.Add(observed)) observed.PropertyChanged += OnSearchDocumentChanged;
            var selected = scope switch
            {
                0 => open.Where(d => ReferenceEquals(d, ActiveSearchDocument)),
                1 => open.Where(d => ReferenceEquals(d, _selectionDocument)),
                3 => open.Where(d => root is not null && SearchPath(d) is { } path
                    && Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
                        + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                _ => open.AsEnumerable()
            };
            var snapshots = selected.Select(d => new SearchDocument(d, SearchPath(d), SearchText(d)!,
                scope == 1 ? _selectionStart : 0, scope == 1 ? _selectionLength : SearchText(d)!.Length)).ToArray();
            if (scope == 1 && _selectionLength == 0) snapshots = [];
            if (snapshots.Sum(snapshot => (long)snapshot.Text.Length) > 64 * 1024 * 1024)
                throw new IOException("Search exceeds the 64 MiB text limit. Narrow the scope.");
            if (scope == 3 && root is null) throw new IOException(LanguageService.Instance.GetString("FindReplace_NoProject"));
            var excluded = open.Select(SearchPath).OfType<string>().Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = await Task.Run(() =>
            {
                var matches = new List<(SearchDocument Document, TextSearchMatch Match)>();
                foreach (var snapshot in snapshots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var match in TextSearchService.Find(snapshot.Text, options, snapshot.Start, snapshot.Length, cancellationToken))
                        matches.Add((snapshot, match));
                    if (matches.Count > TextSearchService.MaximumMatches) throw new IOException("Search exceeds 10000 matches.");
                }
                if (scope == 3 && options.Pattern.Length != 0)
                {
                    foreach (var file in ProjectTextSearchService.Find(root!, options, excluded, cancellationToken))
                    {
                        var snapshot = new SearchDocument(null, file.Path, file.Text, 0, file.Text.Length);
                        foreach (var match in file.Matches) matches.Add((snapshot, match));
                        if (matches.Count > TextSearchService.MaximumMatches) throw new IOException("Search exceeds 10000 matches.");
                    }
                }
                return matches;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _searchMatches.Clear();
            _searchMatches.AddRange(found);
            if (_searchResumeOffset is { } resume)
            {
                _searchMatchIndex = _searchMatches.FindIndex(item => ReferenceEquals(item.Document.Document, _searchResumeDocument)
                    && item.Match.Offset >= resume);
                if (_searchMatchIndex < 0 && _searchMatches.Count > 0) _searchMatchIndex = 0;
                if (_searchMatchIndex >= 0)
                {
                    var hit = _searchMatches[_searchMatchIndex];
                    _searchNavigating = true;
                    try
                    {
                        var document = await ResolveSearchDocumentAsync(hit.Document, cancellationToken);
                        await SelectSearchMatchAsync(document, hit.Document.Text, hit.Match, cancellationToken);
                    }
                    finally { _searchNavigating = false; }
                }
                _searchResumeOffset = null;
            }
            else if (SearchEditor(ActiveSearchDocument) is { } activeEditor)
                _searchMatchIndex = _searchMatches.FindIndex(item => ReferenceEquals(item.Document.Document, ActiveSearchDocument)
                    && item.Match.Offset == activeEditor.SelectionStart && item.Match.Length == activeEditor.SelectionLength);
            UpdateSearchStatus();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _searchMatches.Clear();
                DocumentSearchPanel.SetStatus(ex.Message, false);
            }
        }
        finally { if (!cancellationToken.IsCancellationRequested) _searchBusy = false; }
    }

    private void UpdateSearchStatus()
    {
        var language = LanguageService.Instance;
        var message = _searchMatches.Count == 0 ? language.GetString("FindReplace_NoMatches")
            : string.Format(language.GetString("FindReplace_ResultCount"), _searchMatchIndex + 1, _searchMatches.Count);
        if (_searchMatchIndex >= 0) message += "  " + Path.GetFileName(_searchMatches[_searchMatchIndex].Document.Path);
        if (!string.IsNullOrEmpty(_searchOperationStatus)) message = _searchOperationStatus + "\n" + message;
        DocumentSearchPanel.SetStatus(message, _searchMatches.Count > 0);
    }

    private async void ExecuteSearchAction(SearchAction action)
    {
        if (_searchBusy || _searchActionRunning || _searchMatches.Count == 0) return;
        _searchActionRunning = true;
        var cancellationToken = _documentSearchCancellation?.Token ?? CancellationToken.None;
        try
        {
            if (action is SearchAction.Next or SearchAction.Previous)
            {
                var previous = action == SearchAction.Previous;
                if (_searchMatchIndex < 0)
                {
                    _searchMatchIndex = previous
                        ? _searchMatches.FindLastIndex(item => ReferenceEquals(item.Document.Document, ActiveSearchDocument) && item.Match.Offset < _searchAnchor)
                        : _searchMatches.FindIndex(item => ReferenceEquals(item.Document.Document, ActiveSearchDocument) && item.Match.Offset >= _searchAnchor);
                    if (_searchMatchIndex < 0) _searchMatchIndex = previous ? _searchMatches.Count - 1 : 0;
                }
                else _searchMatchIndex = (_searchMatchIndex + (previous ? -1 : 1) + _searchMatches.Count) % _searchMatches.Count;
                var hit = _searchMatches[_searchMatchIndex];
                var document = await ResolveSearchDocumentAsync(hit.Document, cancellationToken);
                await SelectSearchMatchAsync(document, hit.Document.Text, hit.Match, cancellationToken);
                UpdateSearchStatus();
                return;
            }

            if (_searchMatchIndex < 0 && action == SearchAction.Replace)
            {
                _searchMatchIndex = 0;
                var hit = _searchMatches[0];
                await SelectSearchMatchAsync(await ResolveSearchDocumentAsync(hit.Document, cancellationToken),
                    hit.Document.Text, hit.Match, cancellationToken);
                UpdateSearchStatus();
                return;
            }
            var targets = action == SearchAction.ReplaceAll ? _searchMatches.ToArray() : [_searchMatches[_searchMatchIndex]];
            var groups = targets.GroupBy(hit => hit.Document, (IEqualityComparer<SearchDocument>)ReferenceEqualityComparer.Instance).ToArray();
            if (groups.Length > 100) throw new IOException("Replace all is limited to 100 documents. Narrow the scope.");
            if (groups.Length > 1 && DialogService.GetOwnerWindow() is { } owner
                && !await DialogService.ConfirmAsync(owner, LanguageService.Instance.GetString("FindReplace_ReplaceAll"),
                    string.Format(LanguageService.Instance.GetString("FindReplace_ConfirmReplaceAll"), targets.Length, groups.Length))) return;
            var edits = new List<(IDockable Document, SearchDocument Snapshot, TextSearchMatch[] Matches, string Result)>();
            long replacementSize = 0;
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = await ResolveSearchDocumentAsync(group.Key, cancellationToken);
                var matches = group.Select(item => item.Match).ToArray();
                replacementSize += group.Key.Text.Length + matches.Sum(match => (long)match.Replacement.Length - match.Length);
                if (replacementSize > 64 * 1024 * 1024) throw new IOException("Replacement exceeds the 64 MiB total limit. Narrow the scope.");
                edits.Add((document, group.Key, matches, TextSearchService.Apply(group.Key.Text, matches)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_searchProjectRoot != ProjectContextService.Instance.RootDirectory)
                throw new IOException(LanguageService.Instance.GetString("FindReplace_Changed"));
            foreach (var edit in edits)
                if (SearchText(edit.Document) != edit.Snapshot.Text) throw new IOException(LanguageService.Instance.GetString("FindReplace_Changed"));
            foreach (var edit in edits)
            {
                if (SearchEditor(edit.Document) is { } editor && editor.Text == edit.Snapshot.Text)
                {
                    using (editor.Document.RunUpdate())
                        foreach (var match in edit.Matches.Reverse()) editor.Document.Replace(match.Offset, match.Length, match.Replacement);
                }
                else if (edit.Document is FileViewModel file) file.Text = edit.Result;
                else if (edit.Document is DtbEditorViewModel dtb) dtb.Source = edit.Result;
                if (ReferenceEquals(edit.Document, _selectionDocument) && DocumentSearchPanel.Scope == 1)
                    _selectionLength += edit.Result.Length - edit.Snapshot.Text.Length;
            }
            if (action == SearchAction.Replace)
            {
                _searchResumeDocument = edits[0].Document;
                var match = edits[0].Matches[0];
                _searchResumeOffset = match.Offset + match.Replacement.Length + (match.Length == 0 ? 1 : 0);
            }
            _searchMatches.Clear();
            _searchOperationStatus = string.Format(LanguageService.Instance.GetString("FindReplace_Replaced"), targets.Length);
            DocumentSearchPanel.SetStatus(_searchOperationStatus, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _searchMatches.Clear();
            _searchMatchIndex = -1;
            _searchOperationStatus = ex.Message;
            DocumentSearchPanel.SetStatus(ex.Message, false);
        }
        finally
        {
            _searchActionRunning = false;
            if (action is SearchAction.Replace or SearchAction.ReplaceAll || cancellationToken.IsCancellationRequested) QueueDocumentSearch();
        }
    }

    private async Task<IDockable> ResolveSearchDocumentAsync(SearchDocument snapshot, CancellationToken cancellationToken)
    {
        if (DataContext is not WorkspaceViewModel vm) throw new InvalidOperationException();
        if (_searchProjectRoot != ProjectContextService.Instance.RootDirectory)
            throw new IOException(LanguageService.Instance.GetString("FindReplace_Changed"));
        var open = WorkspaceSessionCapture.Enumerate(vm.Layout).ToArray();
        if (snapshot.Document is { } original && !open.Contains(original))
            throw new IOException(LanguageService.Instance.GetString("FindReplace_Changed"));
        var document = snapshot.Document is { } existing && open.Contains(existing) ? existing
            : open.FirstOrDefault(d => snapshot.Path is not null && SearchText(d) is not null
                && string.Equals(SearchPath(d), snapshot.Path, StringComparison.OrdinalIgnoreCase));
        if (document is null)
        {
            if (snapshot.Path is null) throw new IOException(LanguageService.Instance.GetString("FindReplace_Changed"));
            if (await Task.Run(() => ProjectTextSearchService.ReadText(snapshot.Path, cancellationToken), cancellationToken) != snapshot.Text)
                throw new IOException(LanguageService.Instance.GetString("FindReplace_Changed"));
            await vm.OpenDocumentAsync(snapshot.Path, EditorOpenMode.Text);
            document = vm.FindDocumentsPanePublic()?.ActiveDockable;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (document is null || SearchText(document) != snapshot.Text)
            throw new IOException(LanguageService.Instance.GetString("FindReplace_Changed"));
        if (document.Owner is IDock dock) dock.ActiveDockable = document;
        _searchContextDocument = document;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        cancellationToken.ThrowIfCancellationRequested();
        if (SearchText(document) != snapshot.Text || _searchProjectRoot != ProjectContextService.Instance.RootDirectory)
            throw new IOException(LanguageService.Instance.GetString("FindReplace_Changed"));
        return document;
    }

    private async Task SelectSearchMatchAsync(IDockable document, string snapshot, TextSearchMatch match,
        CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource<TextEditor>(TaskCreationOptions.RunContinuationsAsynchronously);
        void CheckEditor(object? sender, EventArgs args)
        {
            if (SearchText(document) != snapshot)
                ready.TrySetException(new IOException(LanguageService.Instance.GetString("FindReplace_Changed")));
            else if (SearchEditor(document) is { IsEffectivelyVisible: true } current && current.Document.Text == snapshot)
                ready.TrySetResult(current);
        }

        TextEditor editor;
        LayoutUpdated += CheckEditor;
        try
        {
            CheckEditor(this, EventArgs.Empty);
            editor = await ready.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new IOException(LanguageService.Instance.GetString("FindReplace_EditorNotReady"));
        }
        finally { LayoutUpdated -= CheckEditor; }

        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(SearchEditor(document), editor) || !editor.IsEffectivelyVisible
            || editor.Document.Text != snapshot || SearchText(document) != snapshot
            || match.Offset < 0 || match.Length < 0 || match.Offset > editor.Document.TextLength
            || match.Length > editor.Document.TextLength - match.Offset)
            throw new IOException(LanguageService.Instance.GetString("FindReplace_Changed"));
        editor.Select(match.Offset, match.Length);
        var location = editor.Document.GetLocation(match.Offset);
        editor.ScrollTo(location.Line, location.Column);
    }
}