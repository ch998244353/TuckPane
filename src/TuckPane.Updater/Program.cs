using System.Diagnostics;
using System.Text.Json;
using TuckPane.Updates;

if (args.Length != 1) return 2;
UpdateRequest? request = null;
string session = "", backup = "";
try
{
    string requestPath = Path.GetFullPath(args[0]);
    UpdateFiles.NoLinks(requestPath);
    session = Path.GetDirectoryName(requestPath)!;
    request = JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(requestPath)) ?? throw new InvalidDataException("Invalid update request.");
    ReleaseRules.StableVersion(request.Version);
    if (!UpdateFiles.Within(request.PackagePath, session) || UpdateFiles.Within(session, request.TargetDirectory))
        throw new IOException("Updater staging must be outside the program directory.");
    PortablePackage.ValidateTarget(request.TargetDirectory, request.ProtectedPaths);
    UpdateFiles.NoLinks(request.PackagePath);
    UpdateFiles.NoLinks(request.ResultPath);
    if (UpdateFiles.Hash(request.PackagePath) != request.PackageHash) throw new InvalidDataException("Downloaded package was modified.");
    using Process parent = Process.GetProcessById(request.ParentId);
    if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks ||
        !string.Equals(Path.GetDirectoryName(parent.MainModule!.FileName), Path.TrimEndingDirectorySeparator(request.TargetDirectory), StringComparison.OrdinalIgnoreCase))
        throw new IOException("Update parent identity mismatch.");
    backup = Path.Combine(session, "program-backup");
    string staging = Path.Combine(session, "payload");
    if (!request.Installed)
    {
        PortablePackage.Stage(request.PackagePath, staging, request.Version);
        PortablePackage.Read(request.TargetDirectory);
    }
    // Check write access before the user loses the running application.
    string probe = Path.Combine(request.TargetDirectory, ".update-write-" + Guid.NewGuid().ToString("N"));
    using (File.Create(probe)) { }
    File.Delete(probe);
    File.WriteAllText(Path.Combine(session, "ready"), "ready");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    while (!File.Exists(Path.Combine(session, "commit")))
    {
        if (parent.HasExited || File.Exists(Path.Combine(session, "cancel"))) throw new IOException("Update handoff was cancelled.");
        await Task.Delay(100, timeout.Token);
    }
    await parent.WaitForExitAsync(timeout.Token);
    if (File.Exists(Path.Combine(session, "cancel"))) throw new IOException("Update handoff was cancelled.");
    // Check the payload again after waiting; never execute an unverified partial file.
    if (UpdateFiles.Hash(request.PackagePath) != request.PackageHash) throw new InvalidDataException("Package changed during handoff.");
    Report("installing", "Replacing program files after the original process exited.");
    if (request.Installed)
    {
        var start = new ProcessStartInfo(request.PackagePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "/SP-", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCLOSEAPPLICATIONS",
            "/NORESTARTAPPLICATIONS", "/RESTARTEXITCODE=3010", "/DIR=" + request.TargetDirectory, "/LOG=" + Path.Combine(session, "installer.log") })
            start.ArgumentList.Add(argument);
        using Process installer = Process.Start(start) ?? throw new IOException("Installer did not start.");
        await installer.WaitForExitAsync();
        if (installer.ExitCode == 3010) { Report("restart-required", "Installation completed. Restart Windows before opening TuckPane."); return 0; }
        if (installer.ExitCode != 0) throw new IOException($"Installer returned {installer.ExitCode}. Keep the installation log and configuration backup; repair using the previous installer if necessary.");
    }
    else PortablePackage.Apply(staging, request.TargetDirectory, backup, request.ProtectedPaths);
    string executable = Path.Combine(request.TargetDirectory, "TuckPane.exe");
    if (FileVersionInfo.GetVersionInfo(executable).FileVersion != request.Version + ".0")
        throw new IOException("Installed executable version does not match the release.");
    var launch = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = request.TargetDirectory };
    launch.ArgumentList.Add("--startup");
    if (request.TestRoot is not null) launch.Environment["TUCKPANE_TEST_ROOT"] = request.TestRoot;
    // A launch is not proof of a working UI; the new app confirms its version on its next start.
    Report("launched", "Program files updated; awaiting the new application startup.");
    using Process restarted = Process.Start(launch) ?? throw new IOException("The updated application could not be started. Start TuckPane.exe manually.");
    return 0;
}
catch (Exception ex)
{
    if (request is not null)
    {
        try { Report("failed", ex.Message); }
        catch { }
    }
    return 1;
}

void Report(string status, string message)
{
    string configBackup = "";
    try { configBackup = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(request!.ResultPath))?.BackupDirectory ?? ""; }
    catch { }
    UpdateFiles.WriteJson(request!.ResultPath, new UpdateResult(status, request.Version,
        message + (string.IsNullOrEmpty(backup) ? "" : " Program recovery files: " + backup), configBackup));
}
