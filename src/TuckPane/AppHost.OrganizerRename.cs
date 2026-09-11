using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class AppHost
{
    private readonly SemaphoreSlim _organizerNameGate = new(1, 1);

    internal async Task RenameOrganizerAsync(Guid organizerId, string name)
    {
        await _organizerNameGate.WaitAsync();
        try
        {
            OrganizerDefinition definition = State.Organizers.First(item => item.Id == organizerId);
            try { await OrganizerNameChange.RenameAsync(definition, name, SaveStateAsync); }
            finally
            {
                // Refresh the saved name (or its rollback) without rescanning files or cancelling interactions.
                try
                {
                    if (_windows.TryGetValue(organizerId, out MainWindow? window)) window.RefreshOrganizerName();
                    if (definition.ContainerOrganizerId is Guid parentId && _windows.TryGetValue(parentId, out MainWindow? parent))
                        parent.RefreshContainedOrganizerName(organizerId, definition.Name);
                    Console.RefreshOrganizerName(organizerId);
                }
                catch (Exception ex) { AppLogger.Error("收纳窗名称刷新失败。", ex); }
            }
        }
        finally { _organizerNameGate.Release(); }
    }
}
