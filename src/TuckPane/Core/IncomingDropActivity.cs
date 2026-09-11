namespace TuckPane.Core;

// UI-thread ownership shared by organizer and file receiving paths.
internal sealed class IncomingDropActivity
{
    private int _holds;
    internal bool IsActive => _holds > 0;
    internal IDisposable Hold()
    {
        _holds++;
        return new Lease(this);
    }
    private sealed class Lease(IncomingDropActivity activity) : IDisposable
    {
        private IncomingDropActivity? _owner = activity;
        public void Dispose()
        {
            if (_owner is not { } owner) return;
            _owner = null;
            owner._holds--;
        }
    }
    internal bool ProtectsExpansion(bool fileHover) => fileHover || IsActive;
}
