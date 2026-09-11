using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane.Core;

internal static class OrganizerNameChange
{
    internal static string NormalizeName(string requestedName)
    {
        string name = requestedName.Trim();
        if (name.Length == 0) throw new InvalidOperationException(AppStrings.Get("NameRequired"));
        return name;
    }

    // The host serializes name changes. Display names never participate in filesystem operations.
    internal static async Task RenameAsync(OrganizerDefinition definition, string requestedName, Func<Task> persist)
    {
        string name = NormalizeName(requestedName);
        string previousName = definition.Name;
        if (name == previousName) return;
        definition.Name = name;
        try { await persist(); }
        catch
        {
            definition.Name = previousName;
            // An overlapping state save may already have observed the requested name.
            try { await persist(); }
            catch (Exception recoveryFailure) { AppLogger.Error("无法保存收纳窗名称回滚。", recoveryFailure); }
            throw;
        }
    }
}
