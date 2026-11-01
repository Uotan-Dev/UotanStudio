using UotanStudio.Services;

namespace UotanStudio.ViewModels.Compare;

public partial class CompareViewModel
{
    private CompareHexEditSession? _hexEditSession;
    private readonly SemaphoreSlim _hexSaveGate = new(1, 1);

    internal bool IsHexEditorMode => Mode == CompareMode.Binary && _hexEditSession is not null;
    internal byte[]? GetHexBytes(CompareColumnSide side) => side == CompareColumnSide.Right ? _hexEditSession?.Right : _hexEditSession?.Left;

    private async Task<bool> TryLoadHexEditorAsync(CancellationToken ct)
    {
        if (_hexEditSession is null)
        {
            var leftLength = _fileLeftPath.Length == 0 ? 0 : new FileInfo(_fileLeftPath).Length;
            var rightLength = _fileRightPath.Length == 0 ? 0 : new FileInfo(_fileRightPath).Length;
            if (leftLength > CompareHexEditSession.MaximumTotalBytes - rightLength)
            {
                return false;
            }
            var left = await ReadHexBytesAsync(_fileLeftPath, leftLength, ct);
            var right = await ReadHexBytesAsync(_fileRightPath, rightLength, ct);
            var session = await Task.Run(() => new CompareHexEditSession(left, right, ct), ct);
            ct.ThrowIfCancellationRequested();
            _hexEditSession = session;
        }
        _currentHexRows?.Dispose();
        _currentHexRows = new CompareHexRowCollection(_hexEditSession.Left, _hexEditSession.Right);
        Rows = _currentHexRows;
        SelectedIndex = -1;
        UpdateHexSummary();
        NotifyColumnsCompareState();
        return true;
    }

    private static async Task<byte[]> ReadHexBytesAsync(string path, long expectedLength, CancellationToken ct)
    {
        if (path.Length == 0)
        {
            return [];
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedLength || stream.Length > CompareHexEditSession.MaximumTotalBytes)
        {
            throw new IOException("The file length changed while loading the comparison.");
        }
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    internal void HexBytesEdited(CompareColumnSide side, int offset, int count)
    {
        if (_hexEditSession is not { } session || !IsHexEditorMode)
        {
            return;
        }
        session.Update(offset, count);
        _currentHexRows?.InvalidateRange(offset, count);
        if (side == CompareColumnSide.Right)
        {
            session.RightRevision++;
        }
        else
        {
            session.LeftRevision++;
        }
        MarkDirty(side != CompareColumnSide.Right);
        UpdateHexSummary();
    }

    private void UpdateHexSummary()
    {
        if (_hexEditSession is { } session)
        {
            StatsText = string.Format(LanguageService.Instance.GetString("Compare_HexSummary"),
                session.DifferentBytes, session.RangeCount);
        }
    }

    private async Task SaveHexSideAsync(bool leftSide, string path)
    {
        if (_hexEditSession is not { } session)
        {
            return;
        }
        await _hexSaveGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(session, _hexEditSession))
            {
                return;
            }
            await WriteHexSideAsync(session, leftSide, path);
        }
        finally
        {
            _hexSaveGate.Release();
        }
    }

    private async Task WriteHexSideAsync(CompareHexEditSession session, bool leftSide, string path)
    {
        var revision = leftSide ? session.LeftRevision : session.RightRevision;
        var bytes = (byte[])(leftSide ? session.Left : session.Right).Clone();
        var temporary = path + ".save-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            if (ReferenceEquals(session, _hexEditSession))
            {
                if (revision == (leftSide ? session.LeftRevision : session.RightRevision))
                {
                    ClearDirty(leftSide);
                }
                ClearError();
            }
        }
        catch (Exception ex)
        {
            ReportError(UserError.Describe(ex));
            LogService.Instance.Write(LogLevel.Error, "Compare", ex.Message);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}