using TuckPane.Models;
using Windows.ApplicationModel.DataTransfer;

namespace TuckPane.Core;

internal static class IncomingDropOperation
{
    internal static DataPackageOperation ResultOperation(DataPackageOperation requested, DataPackageOperation allowed,
        IReadOnlyList<TransferOutcome> outcomes)
    {
        if (outcomes.Count == 0 || outcomes.Any(x => x.Status is not (TransferStatus.Moved or TransferStatus.Copied or TransferStatus.ShortcutCreated)))
            return DataPackageOperation.None;
        if (outcomes.All(x => x.Status == TransferStatus.ShortcutCreated) && allowed.HasFlag(DataPackageOperation.Link))
            return DataPackageOperation.Link;
        if (requested == DataPackageOperation.Move && outcomes.All(x => x.Status == TransferStatus.Moved))
            return DataPackageOperation.Move;
        // Never ask the source to delete an executable for which only a shortcut was created.
        return allowed.HasFlag(DataPackageOperation.Copy) ? DataPackageOperation.Copy : DataPackageOperation.None;
    }

    internal static async Task RunAsync(Action complete, Func<IDisposable> holdIncoming,
        Func<IDisposable> holdMode, Func<Task> action, Action reset)
    {
        try
        {
            using var incoming = holdIncoming();
            using var mode = holdMode();
            await action();
        }
        finally
        {
            try { reset(); }
            finally { complete(); }
        }
    }
}
