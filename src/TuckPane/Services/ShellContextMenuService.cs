using Microsoft.UI.Xaml;
using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane.Services;

internal static class ShellContextMenuService
{
    internal static bool UsesShellMenu(WidgetItemKind kind) =>
        Enum.IsDefined(kind) && kind is not (WidgetItemKind.Organizer or WidgetItemKind.Note);

    internal static IFileContextMenuSession Create(FrameworkElement anchor, string path, IntPtr commandOwner, FileMenuPopup screenPosition)
    {
        if (!anchor.IsLoaded || anchor.XamlRoot is null)
            throw new InvalidOperationException("The file menu requires a live item.");
        string fullPath = Path.GetFullPath(path);
        // Even an existence probe can block on a disconnected drive. Resolve
        // the Shell item inside the bounded background initialization instead.
        return new Session(anchor, fullPath, commandOwner, screenPosition);
    }

    // No XAML popup or layout measurement: Windows owns the entire menu tree.
    private sealed class Session : IFileContextMenuSession
    {
        private readonly FrameworkElement _anchor;
        private readonly string _path;
        private readonly IntPtr _commandOwner;
        private readonly FileMenuPopup _screenPosition;
        private readonly CancellationTokenSource _lifetime = new();
        private ShellMenuSession? _shell;
        private bool _disposed;

        internal Session(FrameworkElement anchor, string path, IntPtr commandOwner, FileMenuPopup screenPosition)
        {
            _anchor = anchor;
            _path = path;
            _commandOwner = commandOwner;
            _screenPosition = screenPosition;
            _anchor.Unloaded += AnchorUnloaded;
        }

        public async Task<FileMenuSelection?> ShowAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            ShellMenuSession shell = await ShellMenuSession.OpenAsync(_path, _commandOwner, linked.Token);
            if (_disposed || linked.IsCancellationRequested)
            {
                shell.Dispose();
                linked.Token.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(Session));
            }
            _shell = shell;
            return await _shell.ShowClassicAsync(_screenPosition, linked.Token);
        }

        public async Task InvokeAsync(FileMenuSelection selection, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await (_shell ?? throw new InvalidOperationException("No Shell menu session."))
                .InvokeAsync(selection, linked.Token);
        }

        private void AnchorUnloaded(object sender, RoutedEventArgs args) => _lifetime.Cancel();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _anchor.Unloaded -= AnchorUnloaded;
            _lifetime.Cancel();
            _shell?.Dispose();
            _lifetime.Dispose();
        }
    }
}
