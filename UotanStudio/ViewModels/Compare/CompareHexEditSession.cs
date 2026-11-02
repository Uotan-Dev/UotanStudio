using System.Collections;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// Holds bounded editable bytes and updates difference counts only around changed bytes.
/// <para>保存有界可编辑字节，仅在修改范围附近更新差异计数。</para>
/// </summary>
internal sealed class CompareHexEditSession
{
    internal const long MaximumTotalBytes = HexFileEditService.MaximumBytes;
    private readonly BitArray _differences;
    internal byte[] Left { get; }
    internal byte[] Right { get; }
    internal long DifferentBytes { get; private set; }
    internal long RangeCount { get; private set; }
    internal int LeftRevision { get; set; }
    internal int RightRevision { get; set; }

    internal CompareHexEditSession(byte[] left, byte[] right, CancellationToken ct)
    {
        Left = left;
        Right = right;
        _differences = new BitArray(Math.Max(left.Length, right.Length));
        for (var i = 0; i < _differences.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            var different = i >= left.Length || i >= right.Length || left[i] != right[i];
            _differences[i] = different;
            if (different)
            {
                DifferentBytes++;
                if (i == 0 || !_differences[i - 1])
                {
                    RangeCount++;
                }
            }
        }
    }

    internal int Length => _differences.Length;
    internal bool IsDifferent(int offset) => _differences[offset];

    internal void Update(int start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _differences.Length - start);
        var end = start + count;
        var boundary = Math.Min(end + 1, _differences.Length);
        for (var i = start; i < boundary; i++)
        {
            if (_differences[i] && (i == 0 || !_differences[i - 1]))
            {
                RangeCount--;
            }
        }
        for (var i = start; i < end; i++)
        {
            var different = i >= Left.Length || i >= Right.Length || Left[i] != Right[i];
            DifferentBytes += (different ? 1 : 0) - (_differences[i] ? 1 : 0);
            _differences[i] = different;
        }
        for (var i = start; i < boundary; i++)
        {
            if (_differences[i] && (i == 0 || !_differences[i - 1]))
            {
                RangeCount++;
            }
        }
    }
}