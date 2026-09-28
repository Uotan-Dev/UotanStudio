using UotanStudio.Firmware.Patching;

namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    private int _applicableChecks;
    private int _alreadyAppliedChecks;
    private int _notApplicableChecks;

    internal void RecordApplicability(PatchApplicability state)
    {
        CheckOpen();
        RecordCompositeApplicability(state);
        switch (state)
        {
            case PatchApplicability.Applicable:
                Interlocked.Exchange(ref _applicableChecks, 1);
                break;
            case PatchApplicability.AlreadyApplied:
                Interlocked.Exchange(ref _alreadyAppliedChecks, 1);
                break;
            case PatchApplicability.NotApplicable:
                Interlocked.Exchange(ref _notApplicableChecks, 1);
                break;
        }
    }

    private PatchRunOutcome UnchangedOutcome()
    {
        if (_applicableChecks > 0)
        {
            return PatchRunOutcome.NoChanges;
        }
        if (_notApplicableChecks > 0)
        {
            return _alreadyAppliedChecks == 0 && _files.Count == 0
                ? PatchRunOutcome.NotApplicable : PatchRunOutcome.NoChanges;
        }
        return _alreadyAppliedChecks > 0 || _files.Count > 0 ? PatchRunOutcome.AlreadyApplied : PatchRunOutcome.None;
    }
}