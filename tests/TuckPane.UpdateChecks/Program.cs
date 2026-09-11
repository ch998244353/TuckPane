using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TuckPane.Updates;

if (args.Length != 1 || args[0] != "--updates")
{
    Console.WriteLine("Usage: dotnet run --project tests/TuckPane.UpdateChecks -- --updates");
    return 2;
}

(string Name, Func<Task> Run)[] groups =
[
    ("release selection, asset boundaries and 24-hour policy", () => { CheckReleaseRules(); return Task.CompletedTask; }),
    ("download success and failure cleanup", CheckDownloads),
    ("independent configuration backup", () => { CheckBackup(); return Task.CompletedTask; }),
    ("portable replacement, data protection and rollback", () => { CheckPortable(); return Task.CompletedTask; })
];
int failures = 0;
foreach (var group in groups)
{
    try { await group.Run(); Console.WriteLine("PASS " + group.Name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + group.Name + "\n" + error); }
}
Console.WriteLine($"Update checks: {groups.Length - failures}/{groups.Length} groups passed. No GUI, installer or user data accessed.");
return failures == 0 ? 0 : 1;

static void CheckReleaseRules()
{
    foreach (bool installed in new[] { false, true })
    {
        UpdateRelease release = ReleaseRules.Parse(ReleaseJson(), "3.0.2", installed)!;
        Require(release.Version == "3.1.0", "A newer stable release must be selected.");
        Require(release.Package.Name.EndsWith(installed ? "setup.exe" : "portable.zip"), "Wrong distribution selected.");
    }
    foreach (string current in new[] { "3.1.0", "3.2.0" })
        Require(ReleaseRules.Parse(ReleaseJson(), current, false) is null, "Same/older release must not update.");
    Require(ReleaseRules.Parse(ReleaseJson(prerelease: true), "3.0.2", false) is null, "Prerelease accepted.");
    Require(ReleaseRules.Parse(ReleaseJson(draft: true), "3.0.2", false) is null, "Draft accepted.");
    foreach (string malformed in new[] { "3.1.0-beta.1", "03.1.0", "3.1", "../3.1.0" })
        Throws<InvalidDataException>(() => ReleaseRules.StableVersion(malformed));
    foreach (string fault in new[] { "missing", "duplicate", "foreign-url", "zero-size", "wrong-architecture" })
        Throws<InvalidDataException>(() => ReleaseRules.Parse(ReleaseJson(fault), "3.0.2", false));

    DateTimeOffset now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
    Require(ReleaseRules.AutoCheckDue(null, now), "First check was suppressed.");
    Require(!ReleaseRules.AutoCheckDue(now.AddHours(-24).AddTicks(1), now), "Checked before 24 hours.");
    Require(ReleaseRules.AutoCheckDue(now.AddHours(-24), now), "24-hour boundary was suppressed.");
    Require(ReleaseRules.AutoCheckDue(now.AddHours(1), now), "Clock rollback permanently suppressed checks.");
    // Manual checks bypass this policy in the host; that wiring is reviewed separately.
}

static string ReleaseJson(string? fault = null, bool prerelease = false, bool draft = false)
{
    const string prefix = "https://github.com/ch998244353/TuckPane/releases/download/v3.1.0/";
    var assets = new List<Dictionary<string, object>>();
    foreach (string name in new[] { "TuckPane-3.1.0-win-x64-setup.exe", "TuckPane-3.1.0-win-x64-portable.zip", "SHA256SUMS.txt" })
        assets.Add(new() { ["name"] = name, ["browser_download_url"] = prefix + name, ["size"] = 100 });
    switch (fault)
    {
        case "missing": assets.RemoveAt(2); break;
        case "duplicate": assets.Add(new(assets[1])); break;
        case "foreign-url": assets[1]["browser_download_url"] = "https://example.invalid/package.zip"; break;
        case "zero-size": assets[1]["size"] = 0; break;
        case "wrong-architecture": assets[1]["name"] = "TuckPane-3.1.0-win-arm64-portable.zip"; break;
    }
    return JsonSerializer.Serialize(new { tag_name = "v3.1.0", draft, prerelease, body = "Update notes", assets });
}

static async Task CheckDownloads()
{
    byte[] payload = Encoding.UTF8.GetBytes("A deterministic candidate package used only inside a temporary directory.");
    string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    const string name = "TuckPane-3.1.0-win-x64-portable.zip";
    var package = new UpdateAsset(name, "https://example.invalid/package", payload.Length, "sha256:" + hash);
    var sums = new UpdateAsset("SHA256SUMS.txt", "https://example.invalid/sums", 100, null);
    foreach (string scenario in new[] { "success", "cancel", "bad-hash", "bad-digest", "wrong-length", "offline", "timeout" })
    {
        using var scratch = new Scratch();
        using var cancellation = new CancellationTokenSource();
        byte[] served = scenario == "bad-hash" ? Enumerable.Repeat((byte)'X', payload.Length).ToArray() : payload;
        var release = new UpdateRelease("3.1.0", "", package with
        {
            Digest = scenario == "bad-digest" ? "sha256:" + new string('0', 64) : package.Digest,
            Size = scenario == "wrong-length" ? payload.Length + 1 : payload.Length
        }, sums);
        using var handler = new StubHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/sums") return Response(new StringContent(hash + "  " + name + "\n"));
            if (scenario == "offline") throw new HttpRequestException("Simulated offline response.");
            if (scenario == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(new ByteArrayContent(served));
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(150) };
        var client = new ReleaseClient(http);
        IProgress<double>? progress = scenario == "cancel" ? new ImmediateProgress(_ => cancellation.Cancel()) : null;
        if (scenario == "success")
        {
            string path = await client.DownloadAsync(release, scratch.Root, progress, cancellation.Token);
            Require(File.ReadAllBytes(path).SequenceEqual(payload), "Successful download differs from its verified payload.");
            Require(!File.Exists(path + ".download"), "Successful download retained a partial file.");
        }
        else
        {
            if (scenario is "cancel" or "timeout")
                await ThrowsAsync<OperationCanceledException>(() => client.DownloadAsync(release, scratch.Root, progress, cancellation.Token));
            else if (scenario == "offline")
                await ThrowsAsync<HttpRequestException>(() => client.DownloadAsync(release, scratch.Root, progress, cancellation.Token));
            else
                await ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release, scratch.Root, progress, cancellation.Token));
            Require(!Directory.EnumerateFiles(scratch.Root).Any(), scenario + " left an installable or partial package.");
        }
    }
}

static HttpResponseMessage Response(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

static void CheckBackup()
{
    using var scratch = new Scratch();
    string state = Path.Combine(scratch.Root, "configuration", "state.json");
    Directory.CreateDirectory(Path.GetDirectoryName(state)!);
    File.WriteAllText(state, "{\"fixture\":\"window positions and directory associations\"}");
    File.WriteAllText(state + ".bak", "previous state");
    string backupRoot = Path.Combine(scratch.Root, "backups");
    string backup = UpdateFiles.BackupConfiguration(state, backupRoot, "3.0.2");
    Require(File.ReadAllText(Path.Combine(backup, "state.json")) == File.ReadAllText(state), "Configuration backup differs.");
    Require(File.ReadAllText(Path.Combine(backup, "state.json.bak")) == "previous state", "Rolling backup was omitted.");
    Require(File.Exists(Path.Combine(backup, "backup.json")), "Backup recovery metadata is missing.");
    File.WriteAllText(state, "later state");
    string second = UpdateFiles.BackupConfiguration(state, backupRoot, "3.0.2");
    Require(second != backup && File.ReadAllText(Path.Combine(backup, "state.json")) != "later state", "Independent backup was overwritten.");
    string blockedRoot = Path.Combine(scratch.Root, "blocked");
    File.WriteAllText(blockedRoot, "a file blocks the backup directory");
    Throws<IOException>(() => UpdateFiles.BackupConfiguration(state, blockedRoot, "3.0.2"));
    Require(File.ReadAllText(state) == "later state", "A failed backup modified the original configuration.");
}

static void CheckPortable()
{
    foreach (bool failWrite in new[] { false, true })
    {
        using var scratch = new Scratch();
        string target = Path.Combine(scratch.Root, "program");
        string staging = Path.Combine(scratch.Root, "stage");
        string backup = Path.Combine(scratch.Root, "rollback");
        var oldFiles = new Dictionary<string, string> { ["TuckPane.exe"] = "old exe", ["TuckPane.dll"] = "old dll", ["obsolete.dat"] = "obsolete" };
        var newFiles = new Dictionary<string, string> { ["TuckPane.exe"] = "new exe", ["TuckPane.dll"] = "new dll", ["runtime/new.dat"] = "new dependency" };
        WriteProgram(target, "3.0.2", oldFiles);
        File.WriteAllText(Path.Combine(target, "state.json"), "user configuration");
        File.WriteAllText(Path.Combine(target, "extra.txt"), "user file");
        Directory.CreateDirectory(Path.Combine(target, "TuckPane.exe.WebView2"));
        File.WriteAllText(Path.Combine(target, "TuckPane.exe.WebView2", "profile"), "browser data");
        Dictionary<string, string> before = Snapshot(target);
        string zip = Path.Combine(scratch.Root, "candidate.zip");
        WritePackage(zip, "3.1.0", newFiles);
        PortablePackage.Stage(zip, staging, "3.1.0");
        int injected = 0;
        Action<string>? fault = failWrite ? file =>
        {
            if (file == "obsolete.dat" && injected++ == 0) throw new IOException("One injected replacement failure.");
        } : null;
        if (failWrite)
        {
            AggregateException error = Throws<AggregateException>(() => PortablePackage.Apply(staging, target, backup, [], fault));
            Require(injected == 1 && error.InnerExceptions.Count == 1, "Expected one failure followed by successful rollback.");
            Require(SnapshotsEqual(before, Snapshot(target)), "Rollback did not restore all original bytes/remove newly added files.");
        }
        else
        {
            PortablePackage.Apply(staging, target, backup, []);
            Require(PortablePackage.Read(target).Version == "3.1.0", "Updated manifest version was not committed.");
            foreach (var file in newFiles) Require(File.ReadAllText(Path.Combine(target, file.Key)) == file.Value, "Program file was not updated.");
            Require(!File.Exists(Path.Combine(target, "obsolete.dat")), "Obsolete managed file remains.");
            foreach (string data in new[] { "state.json", "extra.txt", "TuckPane.exe.WebView2/profile" })
                Require(Snapshot(target)[data] == before[data], "Extra local data was changed: " + data);
            Require(File.ReadAllText(Path.Combine(backup, "TuckPane.exe")) == "old exe", "Rollback material is missing.");
        }
    }

    using var invalid = new Scratch();
    foreach (string path in new[] { "../escaped.txt", "/absolute.txt", "folder/../../escaped.txt", "C:/escaped.txt", "folder\\escaped.txt" })
    {
        string zip = Path.Combine(invalid.Root, Guid.NewGuid().ToString("N") + ".zip");
        WritePackage(zip, "3.1.0", new() { ["TuckPane.exe"] = "exe", ["TuckPane.dll"] = "dll", ["runtime.dat"] = "runtime" }, path);
        Throws<InvalidDataException>(() => PortablePackage.Stage(zip, Path.Combine(invalid.Root, Guid.NewGuid().ToString("N")), "3.1.0"));
    }
    Require(!File.Exists(Path.Combine(invalid.Root, "escaped.txt")), "A ZIP wrote outside staging.");
    string collisionTarget = Path.Combine(invalid.Root, "program");
    WriteProgram(collisionTarget, "3.0.2", new() { ["TuckPane.exe"] = "exe", ["TuckPane.dll"] = "dll", ["runtime.dat"] = "runtime" });
    Throws<IOException>(() => PortablePackage.ValidateTarget(collisionTarget, [Path.Combine(collisionTarget, "data")]));
}

static void WriteProgram(string root, string version, Dictionary<string, string> files)
{
    Directory.CreateDirectory(root);
    foreach (var file in files)
    {
        string path = Path.Combine(root, file.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.Value);
    }
    UpdateFiles.WriteJson(Path.Combine(root, PortablePackage.ManifestName), new PackageManifest(version,
        files.ToDictionary(file => file.Key, file => UpdateFiles.Hash(Path.Combine(root, file.Key)))));
}

static void WritePackage(string path, string version, Dictionary<string, string> files, string? unsafeEntry = null)
{
    using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
    var hashes = files.ToDictionary(file => file.Key, file => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Value))).ToLowerInvariant());
    foreach (var file in files) WriteEntry(zip, file.Key, file.Value);
    WriteEntry(zip, PortablePackage.ManifestName, JsonSerializer.Serialize(new PackageManifest(version, hashes)));
    if (unsafeEntry is not null) WriteEntry(zip, unsafeEntry, "must not escape");
}

static void WriteEntry(ZipArchive zip, string name, string value)
{
    using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
    writer.Write(value);
}

static Dictionary<string, string> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(root, path).Replace('\\', '/'), UpdateFiles.Hash);
static bool SnapshotsEqual(Dictionary<string, string> left, Dictionary<string, string> right) =>
    left.Count == right.Count && left.All(file => right.TryGetValue(file.Key, out string? hash) && hash == file.Value);
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T error) { return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
sealed class ImmediateProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
sealed class Scratch : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "TuckPane.UpdateChecks-" + Guid.NewGuid().ToString("N"));
    public Scratch() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(Path.GetFullPath(Root)) != expectedParent || !Path.GetFileName(Root).StartsWith("TuckPane.UpdateChecks-"))
            throw new InvalidOperationException("Refusing to clean up outside this test's temporary root.");
        Directory.Delete(Root, recursive: true);
    }
}
