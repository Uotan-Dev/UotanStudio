using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Services;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

public partial class CompareViewModel
{
    private int _hexNavigationOffset = -1;
    internal int HexNavigationOffset => _hexNavigationOffset;

    internal void SetHexNavigationPosition(int offset)
    {
        if (!IsHexEditorMode)
        {
            return;
        }
        SelectedIndex = offset / 16;
        _hexNavigationOffset = offset;
    }

    [RelayCommand]
    private void NextDiff() => NavigateDifference(true);

    [RelayCommand]
    private void PrevDiff() => NavigateDifference(false);

    private void NavigateDifference(bool forward)
    {
        if (Mode is not (CompareMode.Text or CompareMode.Binary) || Rows is null || IsBusy)
        {
            return;
        }

        var current = SelectedIndex;
        var count = Rows.Count;
        Func<int, bool> isDifferent = index => Rows[index].IsLeftDiff || Rows[index].IsRightDiff;
        if (IsHexEditorMode && _hexEditSession is { } session)
        {
            current = _hexNavigationOffset >= 0 && _hexNavigationOffset / 16 == SelectedIndex
                ? _hexNavigationOffset : SelectedIndex < 0 ? -1 : SelectedIndex * 16;
            count = session.Length;
            isDifferent = session.IsDifferent;
        }

        else if (Rows is CompareHexRowCollection hexRows)
        {
            isDifferent = hexRows.IsRowDifferent;
        }

        var target = DifferenceNavigation.Find(count, current, forward, isDifferent);
        if (target < 0)
        {
            var hasDifferences = IsHexEditorMode
                ? _hexEditSession!.DifferentBytes > 0
                : DifferenceNavigation.Find(count, -1, true, isDifferent) >= 0;
            var key = !hasDifferences ? "Compare_NoDifferences"
                : forward ? "Compare_LastDifference" : "Compare_FirstDifference";
            WeakReferenceMessenger.Default.Send(new CompareNavigationNoticeMessage(this, LanguageService.Instance.GetString(key)));
            return;
        }

        if (IsHexEditorMode)
        {
            SetHexNavigationPosition(target);
            target /= 16;
        }
        else
        {
            SelectedIndex = target;
        }
        ScrollToIndex = -1;
        ScrollToIndex = target;
    }
}