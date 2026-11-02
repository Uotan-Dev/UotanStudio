using UotanStudio.Services;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

public sealed partial class CompareColumnViewModel
{
    private byte[]? _browseHexBytes;
    private string? _browseHexPath;
    private int _browseHexRevision;

    /// <summary>Whether this browsing column has editable binary content.<para>本浏览列是否具有可编辑的二进制内容。</para></summary>
    public bool IsBrowsingHexEditor => IsBrowsing && _browseHexBytes is not null;

    /// <summary>Whether this column displays the shared hex editor.<para>本列是否显示共享十六进制编辑器。</para></summary>
    public bool IsHexEditorVisible => IsCompareHexEditorVisible || IsBrowsingHexEditor;

    /// <summary>Whether this column displays a hex comparison editor.<para>本列是否显示十六进制对比编辑器。</para></summary>
    public bool IsCompareHexEditorVisible => IsCompareSide && _owner.IsHexEditorMode;

    /// <summary>Whether the displayed bytes exceed the editing limit.<para>当前字节内容是否超出编辑上限。</para></summary>
    public bool ShowHexReadOnlyHint => !IsHexEditorVisible && Rows is CompareHexRowCollection;

    /// <summary>The editable bytes owned by this column or comparison side.<para>本列或对比侧持有的可编辑字节。</para></summary>
    public byte[]? HexBytes => IsCompareSide ? _owner.GetHexBytes(Side) : _browseHexBytes;

    /// <summary>The comparison counterpart, or null for independent browsing.<para>对比的对侧字节；独立浏览时为 null。</para></summary>
    public byte[]? OtherHexBytes => IsCompareSide
        ? _owner.GetHexBytes(UsesRightCells ? CompareColumnSide.Left : CompareColumnSide.Right)
        : null;

    internal void HexBytesEdited(int offset, int count)
    {
        if (IsCompareSide)
        {
            _owner.HexBytesEdited(Side, offset, count);
        }
        else if (IsBrowsingHexEditor)
        {
            _browseHexRevision++;
            MarkDirty();
        }
    }

    private async Task LoadBrowseHexAsync(string path, CancellationToken ct)
    {
        var length = new FileInfo(path).Length;
        if (length > HexFileEditService.MaximumBytes)
        {
            ct.ThrowIfCancellationRequested();
            Rows = new CompareHexRowCollection(path, string.Empty, []);
            return;
        }
        var bytes = await HexFileEditService.ReadAsync(path, length, ct);
        ct.ThrowIfCancellationRequested();
        _browseHexBytes = bytes;
        _browseHexPath = System.IO.Path.GetFullPath(path);
        _browseHexRevision = 0;
        Rows = null;
        NotifyHexStateChanged();
    }

    private async Task SaveBrowseHexAsync()
    {
        if (_browseHexBytes is not { } bytes || _browseHexPath is not { } path)
        {
            return;
        }
        var revision = _browseHexRevision;
        var ct = _loadCts.Token;
        var snapshot = (byte[])bytes.Clone();
        try
        {
            await HexFileEditService.SaveAsync(path, snapshot, ct);
            if (ReferenceEquals(bytes, _browseHexBytes) && IsBrowsing)
            {
                if (revision == _browseHexRevision)
                {
                    _isDirty = false;
                    NotifyDirtyChanged();
                }
                _owner.ClearError();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _owner.ReportError(UserError.Describe(ex));
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
    }

    private void ClearBrowseHex()
    {
        if (_browseHexBytes is null)
        {
            return;
        }
        _browseHexBytes = null;
        _browseHexPath = null;
        _browseHexRevision = 0;
        _isDirty = false;
        NotifyHexStateChanged();
        NotifyDirtyChanged();
    }

    private void NotifyHexStateChanged()
    {
        OnPropertyChanged(nameof(IsBrowsingHexEditor));
        OnPropertyChanged(nameof(IsCompareHexEditorVisible));
        OnPropertyChanged(nameof(IsHexEditorVisible));
        OnPropertyChanged(nameof(ShowHexReadOnlyHint));
        OnPropertyChanged(nameof(HexBytes));
        OnPropertyChanged(nameof(OtherHexBytes));
        OnPropertyChanged(nameof(ShowHint));
        OnPropertyChanged(nameof(ShowLeftCells));
        OnPropertyChanged(nameof(ShowRightCells));
    }
}