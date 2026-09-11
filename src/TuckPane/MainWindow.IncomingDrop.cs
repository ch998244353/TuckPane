using TuckPane.Core;
namespace TuckPane;

public sealed partial class MainWindow
{
    private readonly IncomingDropActivity _incomingDrop = new();
    private bool ReceivingDrop => _incomingDrop.ProtectsExpansion(_dragHasLocalFile);
    internal IDisposable HoldIncomingDrop()
    {
        _ordinaryOutsideSince = 0;
        return _incomingDrop.Hold();
    }
}
