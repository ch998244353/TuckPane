using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

// Publication is the irreversible boundary: source deletion may have partially succeeded.
internal static class PublishedMoveCompletion
{
    internal static TransferOutcome Complete(string source, string destination, Action deleteSource, Action? cleanupParent = null)
    {
        try
        {
            deleteSource();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Published move source cleanup failed", ex);
            return new(source, destination, TransferStatus.CopiedSourceRetained,
                AppStrings.Format("PublishedCopyRetained", destination));
        }
        try { cleanupParent?.Invoke(); }
        catch (Exception ex)
        {
            AppLogger.Error("Published move parent cleanup failed", ex);
            return new(source, destination, TransferStatus.Moved, AppStrings.Get("MoveParentCleanupWarning"));
        }
        return new(source, destination, TransferStatus.Moved, AppStrings.Get("Moved"));
    }
}
