namespace TuckPane.Core;

// Kept independent of WinUI so the same file transaction can be checked without opening windows.
internal static class NoteRenameTransaction
{
    internal static async Task RenameAsync(
        string source,
        string target,
        Func<Task> beginRename,
        Action endRename,
        Action<string, string> rekey,
        Action<string> rebind,
        Func<Task> persist)
    {
        // Finish any in-flight write and hold the note's existing save gate until binding settles.
        await beginRename();
        try
        {
            File.Move(source, target);
            try
            {
                rekey(source, target);
                rebind(target);
                await persist();
            }
            catch (Exception saveError)
            {
                try
                {
                    File.Move(target, source);
                }
                catch (Exception rollbackError)
                {
                    // The new file still owns the document. Never point the editor at an old-path occupant.
                    rebind(target);
                    throw new IOException(
                        "The note keeps its new name because saving settings and restoring the original name both failed.",
                        new AggregateException(saveError, rollbackError));
                }
                rekey(target, source);
                rebind(source);
                throw;
            }
        }
        finally
        {
            endRename();
        }
    }
}
