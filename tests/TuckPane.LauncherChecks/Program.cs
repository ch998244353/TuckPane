using System.Diagnostics;
using System.Text.Json;
using TuckPane.Launcher;
using TuckPane.Updates;

if (args.FirstOrDefault() == "--record")
{
    File.WriteAllText(args[1], JsonSerializer.Serialize(new Receipt(
        Environment.ProcessId, Environment.ProcessPath!, Environment.CurrentDirectory, args[2..],
        Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT"))));
    return 0;
}

string package = Path.GetFullPath(args.Single());
string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
string root = Path.GetFullPath(Path.Combine(temp, "TuckPane-launcher-" + Guid.NewGuid().ToString("N")));
Require(root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root), "Unsafe test directory.");
string[] launcherFiles = ["00-启动 TuckPane.exe", "TuckPane.Launcher.dll",
    "TuckPane.Launcher.deps.json", "TuckPane.Launcher.runtimeconfig.json"];
Directory.CreateDirectory(root);
try
{
    PackageManifest published = PortablePackage.Read(package);
    foreach (string file in launcherFiles.Append("TuckPane.pri"))
        Require(published.Files.TryGetValue(file, out string? hash) &&
            UpdateFiles.Hash(Path.Combine(package, file)).Equals(hash, StringComparison.OrdinalIgnoreCase),
            "Missing or corrupt published file: " + file);
    Require(UpdateFiles.Hash(Path.Combine(package, launcherFiles[0])) !=
        UpdateFiles.Hash(Path.Combine(package, "TuckPane.exe")), "Renamed application shipped as launcher.");
    Console.WriteLine("PASS: published launcher and PRI hashes are covered by the update manifest");

    string target = Path.Combine(root, "中文 空格便携包");
    string caller = Path.Combine(root, "不同 工作目录");
    Directory.CreateDirectory(target);
    Directory.CreateDirectory(caller);
    foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    string recorder = Path.Combine(target, "TuckPane.LauncherChecks.exe");
    string canonical = Path.Combine(target, "TuckPane.exe");
    string alias = Path.Combine(target, launcherFiles[0]);
    File.Copy(recorder, canonical);
    File.Copy(recorder, alias);
    File.WriteAllText(Path.Combine(target, "TuckPane.dll"), "update fixture only");
    string[] oldFiles = ["TuckPane.exe", "TuckPane.dll", launcherFiles[0]];
    WriteManifest(target, "4.2.0", oldFiles);
    File.WriteAllText(Path.Combine(target, "user-extra.txt"), "preserve me");

    // Red control reproduces the former packaging pattern without loading any WinUI code.
    Receipt old = Record(alias, caller, []);
    Require(old.ProcessPath == alias, "Old packaging control did not execute the renamed application.");
    Console.WriteLine("RED control: copied EXE runs under the alias name, not TuckPane.exe");

    string staging = Path.Combine(root, "staging");
    Directory.CreateDirectory(staging);
    File.Copy(canonical, Path.Combine(staging, "TuckPane.exe"));
    File.Copy(Path.Combine(target, "TuckPane.dll"), Path.Combine(staging, "TuckPane.dll"));
    foreach (string file in launcherFiles) File.Copy(Path.Combine(package, file), Path.Combine(staging, file));
    string[] nextFiles = ["TuckPane.exe", "TuckPane.dll", .. launcherFiles];
    WriteManifest(staging, "4.2.1", nextFiles);
    string oldHash = UpdateFiles.Hash(alias);
    bool failed = false;
    try
    {
        PortablePackage.Apply(staging, target, Path.Combine(root, "failed-backup"), [], name =>
        {
            if (name == "TuckPane.Launcher.dll") throw new IOException("Injected update interruption");
        });
    }
    catch (AggregateException) { failed = true; }
    Require(failed && UpdateFiles.Hash(alias) == oldHash && PortablePackage.Read(target).Version == "4.2.0",
        "Failed update did not restore the old launcher and manifest.");
    Console.WriteLine("PASS: interrupted launcher replacement restores the previous entry and manifest");

    PortablePackage.Apply(staging, target, Path.Combine(root, "backup"), []);
    Require(File.ReadAllText(Path.Combine(target, "user-extra.txt")) == "preserve me", "User file was changed.");
    Require(UpdateFiles.Hash(alias) == published.Files[launcherFiles[0]], "Old entry was not replaced.");
    Require(!File.Exists(Path.Combine(target, "00-启动 TuckPane.pri")), "Test must not depend on alias PRI.");
    Console.WriteLine("PASS: update replaces the old entry, adds launcher dependencies and preserves extra files");

    foreach (string[] forwarded in new[]
    {
        Array.Empty<string>(),
        new[] { "--create-organizer", "中文 文件夹", "", "a\"b", @"C:\尾部 空格\", "relative file.tucknote", "--startup" }
    })
    {
        Receipt receipt = Record(alias, caller, forwarded);
        Require(receipt.ProcessPath == canonical, "Launcher did not execute the canonical application.");
        Require(receipt.WorkingDirectory == caller, "Relative argument resolution changed.");
        Require(receipt.Arguments.SequenceEqual(forwarded), "Arguments changed during forwarding.");
        Require(receipt.TestRoot == root, "Child environment was not inherited.");
    }
    // With the launcher process gone, its file can be replaced by the updater.
    using (File.Open(alias, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    Console.WriteLine("PASS: real packaged launcher forwards arguments/environment to TuckPane.exe and releases its file");

    // The linked production failure path reports once and returns nonzero, without displaying a dialog.
    Require(!File.Exists(Path.Combine(AppContext.BaseDirectory, "TuckPane.exe")), "Failure test requires no sibling app.");
    int reports = 0;
    int code = PortableLauncher.Run([], _ => reports++);
    Require(code == 1 && reports == 1, "Missing application must report failure and return 1.");
    Console.WriteLine("PASS: missing application reports a launch failure with nonzero status");
    return 0;
}
finally
{
    // Only the uniquely created test directory is removed; no user data or application is launched.
    Directory.Delete(root, recursive: true);
}

Receipt Record(string executable, string workingDirectory, string[] forwarded)
{
    string receiptPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory };
    start.Environment["TUCKPANE_TEST_ROOT"] = root;
    start.ArgumentList.Add("--record");
    start.ArgumentList.Add(receiptPath);
    foreach (string arg in forwarded) start.ArgumentList.Add(arg);
    using Process process = Process.Start(start)!;
    if (!process.WaitForExit(10000))
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        throw new Exception("Launcher did not exit promptly.");
    }
    Require(process.ExitCode == 0, "Launcher exited with " + process.ExitCode);
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromSeconds(10))
    {
        try
        {
            if (File.Exists(receiptPath))
            {
                Receipt receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptPath))!;
                try
                {
                    using Process child = Process.GetProcessById(receipt.ProcessId);
                    Require(child.WaitForExit(5000), "Recorder did not exit after writing its receipt.");
                }
                catch (ArgumentException) { /* The recorder has already exited. */ }
                return receipt;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        Thread.Sleep(20);
    }
    throw new Exception("Canonical child did not write its startup receipt.");
}

static void WriteManifest(string directory, string version, IEnumerable<string> files) =>
    File.WriteAllText(Path.Combine(directory, PortablePackage.ManifestName), JsonSerializer.Serialize(
        new PackageManifest(version, files.ToDictionary(name => name, name => UpdateFiles.Hash(Path.Combine(directory, name))))));

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

internal sealed record Receipt(int ProcessId, string ProcessPath, string WorkingDirectory, string[] Arguments, string? TestRoot);
