using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using A3.OfflineStudio;

// A copy of this test executable can serve a minimal loopback-only fake runtime.
if (args.Contains("--api-key")) { await RunFakeRuntime(args); return; }
if (args.Length == 3 && args[0] == "--fake-parent")
{
    using var childHost = new ModelHost();
    await childHost.StartAsync(args[1], FakeManifest());
    await File.WriteAllTextAsync(args[2], "ready");
    await Task.Delay(Timeout.Infinite);
    return;
}

// Runs against linked production sources. No real model, NuGet packages or internet access.
var root = Path.Combine(Path.GetTempPath(), "A3Integration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
StudioSettings.StateRoot = Path.Combine(root, "state");
_ = StudioSettings.Load();
var manager = new BundleManager();
var failures = new List<string>();
int passed = 0;
try
{
    var valid = CreateBundle("valid");
    var installed = await manager.ImportAsync(valid);
    var (installedDirectory, installedManifest) = manager.Read(installed);
    var settings = new StudioSettings { ActiveBundle = installed, PreviousBundle = "prior-model", RepositoryPath = "C:\\A3" };
    settings.Save();
    var settingsBytes = File.ReadAllBytes(Path.Combine(StudioSettings.StateRoot, "settings.json"));
    var initialModel = File.ReadAllBytes(Path.Combine(installedDirectory, "models/model.gguf"));

    await Check("valid bundle import preserves exact payload", () =>
    {
        Assert(initialModel.SequenceEqual(Encoding.UTF8.GetBytes("GGUF fixture model")), "Model bytes changed.");
        Assert(File.Exists(Path.Combine(installedDirectory, "runtime/llama-server.exe")), "Runtime was not installed.");
        Assert(installedManifest.Model.ContextSize == 4096, "Context size changed.");
        return Task.CompletedTask;
    });
    await Check("source provenance survives install serialization", () =>
    {
        Assert(installedManifest.Sources is { } source && source.ValueKind == JsonValueKind.Array && source[0].GetProperty("url").GetString() == "https://publisher.invalid/pinned-model", "Sources metadata was lost.");
        return Task.CompletedTask;
    });
    await Check("settings save/load preserves active and previous IDs", () =>
    {
        var loaded = StudioSettings.Load();
        Assert(loaded.ActiveBundle == installed && loaded.PreviousBundle == "prior-model", "Settings changed.");
        return Task.CompletedTask;
    });
    await Check("corrupt settings are preserved and recovered without startup failure", () =>
    {
        var file = Path.Combine(StudioSettings.StateRoot, "settings.json");
        try
        {
            File.WriteAllText(file, "{this is broken json");
            var recovered = StudioSettings.Load();
            Assert(recovered.ActiveBundle is null, "Corrupt settings were unexpectedly reused.");
            Assert(Directory.EnumerateFiles(StudioSettings.StateRoot, "settings.corrupt-*.json").Any(p => File.ReadAllText(p) == "{this is broken json"), "Corrupt settings backup was lost.");
        }
        finally { File.WriteAllBytes(file, settingsBytes); }
        return Task.CompletedTask;
    });

    await Reject("tampered model hash", CreateBundle("tampered-model", alter: m => m.Files.Single(f => f.Path == "models/model.gguf").Sha256 = new string('0', 64)));
    await Reject("tampered runtime hash", CreateBundle("tampered-runtime", alter: m => m.Files.Single(f => f.Path == "runtime/llama-server.exe").Sha256 = new string('0', 64)));
    await Reject("model metadata hash differs from file list", CreateBundle("model-metadata", alter: m => m.Model.Sha256 = new string('f', 64)));
    await Reject("traversal payload", CreateBundle("traversal", extras: [new("../escaped.txt", "escape")], listExtras: true));
    await Reject("absolute drive payload", CreateBundle("absolute-drive", extras: [new("C:/escape.txt", "escape")], listExtras: true));
    await Reject("absolute root payload", CreateBundle("absolute-root", extras: [new("/escape.txt", "escape")], listExtras: true));
    await Reject("backslash payload", CreateBundle("backslash", extras: [new("models\\escape.txt", "escape")], listExtras: true));
    await Reject("trailing-dot path alias", CreateBundle("trailing-dot", extras: [new("models./escape.txt", "escape")], listExtras: true));
    await Reject("duplicate ZIP entry", CreateBundle("duplicate", extras: [new("models/model.gguf", "duplicate")], listExtras: false));
    await Reject("case-insensitive duplicate ZIP entry", CreateBundle("duplicate-case", extras: [new("MODELS/MODEL.GGUF", "duplicate")], listExtras: false));
    await Reject("unexpected unlisted file", CreateBundle("unexpected", extras: [new("extra.txt", "unlisted")], listExtras: false));
    await Reject("missing manifest payload", CreateBundle("missing", alter: m => m.Files.Add(new() { Path = "models/missing.gguf", Size = 1, Sha256 = new string('0', 64) })));
    await Reject("symbolic-link file attributes", CreateBundle("symlink", symlink: true));
    await Reject("unsupported schema", CreateBundle("schema", alter: m => m.SchemaVersion = 200));
    await Reject("too-large context", CreateBundle("context", alter: m => m.Model.ContextSize = 1_000_000));
    await Reject("forged ZIP uncompressed length cannot bypass limits", CreateForgedLengthBundle());
    await Check("cancelled import cleans staging and preserves active", async () =>
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Throws<OperationCanceledException>(() => manager.ImportAsync(valid, cancel.Token));
        AssertNoStaging();
        AssertOldState();
    });
    await Check("bundle read rejects parent paths", () =>
    {
        ThrowsSync<InvalidDataException>(() => manager.Read("../outside"));
        return Task.CompletedTask;
    });

    const string wordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string sheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string relationNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    await Check("Word extracts Korean paragraphs, tabs and breaks", () =>
    {
        var path = CreateOffice("korean.docx", new() { ["word/document.xml"] = $"<w:document xmlns:w=\"{wordNs}\"><w:body><w:p><w:r><w:t>모터 운전</w:t><w:tab/><w:t>상태</w:t><w:br/><w:t>정지</w:t></w:r></w:p><w:p><w:r><w:t>다음 문단</w:t></w:r></w:p></w:body></w:document>" });
        var text = DocumentExtractor.Extract(path);
        Assert(text.Contains("모터 운전\t상태\n정지") && text.Contains("다음 문단"), "Word text semantics changed.");
        return Task.CompletedTask;
    });
    await Check("Excel extracts shared/inline strings and cached formula values", () =>
    {
        var path = CreateOffice("cells.xlsx", new()
        {
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{sheetNs}\" xmlns:r=\"{relationNs}\"><sheets><sheet name=\"설정\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\" Type=\"worksheet\"/></Relationships>",
            ["xl/sharedStrings.xml"] = $"<sst xmlns=\"{sheetNs}\"><si><r><t>모터</t></r><r><t>설정</t></r></si></sst>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{sheetNs}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>준비</t></is></c><c r=\"C1\"><f>SUM(20,22)</f><v>42</v></c></row></sheetData></worksheet>"
        });
        var text = DocumentExtractor.Extract(path);
        Assert(text.Contains("[시트: 설정]") && text.Contains("A1=모터설정") && text.Contains("B1=준비") && text.Contains("C1=42"), "Excel cell values changed.");
        Assert(!text.Contains("SUM(20,22)"), "Formula was interpreted instead of using cached value.");
        return Task.CompletedTask;
    });
    await Check("Excel external worksheet relationships are ignored", () =>
    {
        var path = CreateOffice("external.xlsx", new()
        {
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{sheetNs}\" xmlns:r=\"{relationNs}\"><sheets><sheet name=\"외부\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships><Relationship Id=\"rId1\" TargetMode=\"External\" Target=\"https://example.invalid/sheet.xml\"/></Relationships>"
        });
        Assert(DocumentExtractor.Extract(path).Contains("[시트: 외부]"), "Workbook sheet header missing.");
        return Task.CompletedTask;
    });
    var secret = Path.Combine(root, "secret.txt"); File.WriteAllText(secret, "DO-NOT-READ-EXTERNAL-ENTITY");
    var entity = new Uri(secret).AbsoluteUri;
    await Check("Word rejects XXE before reading an external entity", () =>
    {
        var doc = $"<!DOCTYPE w:document [<!ENTITY xxe SYSTEM \"{entity}\">]><w:document xmlns:w=\"{wordNs}\"><w:body><w:p><w:r><w:t>&xxe;</w:t></w:r></w:p></w:body></w:document>";
        var path = CreateOffice("xxe.docx", new() { ["word/document.xml"] = doc });
        ThrowsSync<XmlException>(() => DocumentExtractor.Extract(path));
        return Task.CompletedTask;
    });
    await Check("Excel rejects workbook XXE", () =>
    {
        var doc = $"<!DOCTYPE workbook [<!ENTITY xxe SYSTEM \"{entity}\">]><workbook xmlns=\"{sheetNs}\"><sheets>&xxe;</sheets></workbook>";
        var path = CreateOffice("xxe.xlsx", new() { ["xl/workbook.xml"] = doc });
        ThrowsSync<XmlException>(() => DocumentExtractor.Extract(path));
        return Task.CompletedTask;
    });
    await Check("Excel rejects traversal relationships", () =>
    {
        var path = CreateOffice("sheet-traversal.xlsx", new()
        {
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{sheetNs}\" xmlns:r=\"{relationNs}\"><sheets><sheet name=\"bad\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships><Relationship Id=\"rId1\" Target=\"../outside.xml\"/></Relationships>"
        });
        ThrowsSync<InvalidDataException>(() => DocumentExtractor.Extract(path));
        return Task.CompletedTask;
    });

    await Check("model host stops its own runtime process", async () =>
    {
        var fixture = CreateFakeRuntime("stop-runtime", exitAfterReady: false);
        using var host = new ModelHost();
        await host.StartAsync(fixture.Directory, fixture.Manifest);
        Assert(host.Ready, "Fake runtime never became ready.");
        var pid = int.Parse(File.ReadAllText(Path.Combine(fixture.Directory, "runtime/pid.txt")));
        host.Stop();
        Assert(!IsRunning(pid) && !host.Ready, "Stop left the child runtime running.");
    });
    await Check("model host clears readiness after unexpected runtime exit", async () =>
    {
        var fixture = CreateFakeRuntime("exit-runtime", exitAfterReady: true);
        using var host = new ModelHost();
        await host.StartAsync(fixture.Directory, fixture.Manifest);
        var pid = int.Parse(File.ReadAllText(Path.Combine(fixture.Directory, "runtime/pid.txt")));
        for (int i = 0; i < 40 && IsRunning(pid); i++) await Task.Delay(100);
        Assert(!IsRunning(pid), "Fake runtime did not terminate as expected.");
        Assert(!host.Ready, "Ready stayed true after the runtime process exited.");
    });
    await Check("Windows job stops runtime after abrupt parent termination", async () =>
    {
        var fixture = CreateFakeRuntime("abrupt-parent", exitAfterReady: false);
        var signal = Path.Combine(root, "fake-parent-ready.txt");
        var appHost = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(typeof(Manifest).Assembly.Location) + ".exe");
        var info = new ProcessStartInfo(appHost) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--fake-parent"); info.ArgumentList.Add(fixture.Directory); info.ArgumentList.Add(signal);
        using var parent = Process.Start(info) ?? throw new IOException("Cannot start the fixture parent.");
        int childPid = 0;
        try
        {
            for (int i = 0; i < 100 && !File.Exists(signal) && !parent.HasExited; i++) await Task.Delay(100);
            Assert(File.Exists(signal), "Fixture parent never became ready.");
            childPid = int.Parse(File.ReadAllText(Path.Combine(fixture.Directory, "runtime/pid.txt")));
            parent.Kill(); // Deliberately kill only the parent: job ownership must kill its runtime.
            await parent.WaitForExitAsync();
            for (int i = 0; i < 40 && IsRunning(childPid); i++) await Task.Delay(100);
            Assert(!IsRunning(childPid), "Runtime survived abrupt parent termination.");
        }
        finally
        {
            if (!parent.HasExited) parent.Kill(true);
            if (childPid > 0 && IsRunning(childPid)) { using var orphan = Process.GetProcessById(childPid); orphan.Kill(true); }
        }
    });

    Console.WriteLine($"RESULT: {passed} passed, {failures.Count} failed.");
    if (failures.Count > 0) { foreach (var failure in failures) Console.Error.WriteLine(failure); Environment.ExitCode = 1; }

    async Task Reject(string name, string archive)
    {
        await Check(name, async () =>
        {
            await Throws<InvalidDataException>(() => manager.ImportAsync(archive));
            AssertNoStaging();
            AssertOldState();
        });
    }
    void AssertNoStaging() => Assert(!Directory.EnumerateDirectories(manager.BundlesRoot, "stage-*").Any(), "Failed import leaked staging files.");
    void AssertOldState()
    {
        Assert(File.ReadAllBytes(Path.Combine(installedDirectory, "models/model.gguf")).SequenceEqual(initialModel), "Previously installed model changed.");
        Assert(File.ReadAllBytes(Path.Combine(StudioSettings.StateRoot, "settings.json")).SequenceEqual(settingsBytes), "Active settings changed after failed import.");
    }
}
finally
{
    // Only delete this invocation's newly created test directory.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("A3Integration-", StringComparison.Ordinal)) Directory.Delete(root, true);
}

async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures.Add("FAIL " + name + ": " + e.Message); Console.Error.WriteLine(failures[^1]); }
}
static void Assert(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
}
static void ThrowsSync<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
}
string CreateOffice(string name, Dictionary<string, string> parts)
{
    var path = Path.Combine(root, name);
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var part in parts) { using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false)); writer.Write(part.Value); }
    return path;
}
string CreateBundle(string name, Action<Manifest>? alter = null, Extra[]? extras = null, bool listExtras = false, bool symlink = false)
{
    var model = Encoding.UTF8.GetBytes("GGUF fixture model");
    var runtime = Encoding.UTF8.GetBytes("MZ fixture runtime");
    var files = new List<Payload> { new("models/model.gguf", model), new("runtime/llama-server.exe", runtime) };
    var manifest = new Manifest
    {
        Model = new() { Name = "Fixture", File = "models/model.gguf", Sha256 = Hash(model), ContextSize = 4096 },
        Runtime = new() { File = "runtime/llama-server.exe" },
        Files = files.Select(p => new ManifestFile { Path = p.Path, Size = p.Bytes.Length, Sha256 = Hash(p.Bytes) }).ToList()
    };
    if (listExtras && extras is not null) foreach (var extra in extras) { var bytes = Encoding.UTF8.GetBytes(extra.Content); manifest.Files.Add(new() { Path = extra.Path, Size = bytes.Length, Sha256 = Hash(bytes) }); }
    alter?.Invoke(manifest);
    var path = Path.Combine(root, name + ".zip");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var file in files)
    {
        var entry = zip.CreateEntry(file.Path, CompressionLevel.NoCompression);
        if (symlink && file.Path.StartsWith("models/")) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
        using var stream = entry.Open(); stream.Write(file.Bytes);
    }
    if (extras is not null) foreach (var extra in extras) { using var writer = new StreamWriter(zip.CreateEntry(extra.Path).Open(), new UTF8Encoding(false)); writer.Write(extra.Content); }
    using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false))) writer.Write(JsonSerializer.Serialize(manifest, StudioSettings.Json));
    return path;
}
string CreateForgedLengthBundle()
{
    var path = Path.Combine(root, "forged-size.zip");
    var bytes = new byte[2 * 1024 * 1024]; Array.Fill(bytes, (byte)'x');
    var modelHash = Hash(bytes);
    var runtime = Encoding.UTF8.GetBytes("MZ fixture runtime");
    var manifest = new Manifest
    {
        Model = new() { Name = "Forged", File = "models/model.gguf", Sha256 = modelHash, ContextSize = 4096 },
        Runtime = new() { File = "runtime/llama-server.exe" },
        Files = [new() { Path = "models/model.gguf", Size = 1, Sha256 = modelHash }, new() { Path = "runtime/llama-server.exe", Size = runtime.Length, Sha256 = Hash(runtime) }]
    };
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
    {
        using (var stream = zip.CreateEntry("models/model.gguf", CompressionLevel.Optimal).Open()) stream.Write(bytes);
        using (var stream = zip.CreateEntry("runtime/llama-server.exe").Open()) stream.Write(runtime);
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)); writer.Write(JsonSerializer.Serialize(manifest, StudioSettings.Json));
    }
    var raw = File.ReadAllBytes(path);
    // ZIP local/central uncompressed length is forged; compressed payload stays intact.
    for (int i = 0; i + 46 < raw.Length; i++)
    {
        if (BitConverter.ToUInt32(raw, i) == 0x04034b50u && NameAt(raw, i + 30, BitConverter.ToUInt16(raw, i + 26)) == "models/model.gguf") BitConverter.GetBytes(1u).CopyTo(raw, i + 22);
        if (BitConverter.ToUInt32(raw, i) == 0x02014b50u && NameAt(raw, i + 46, BitConverter.ToUInt16(raw, i + 28)) == "models/model.gguf") BitConverter.GetBytes(1u).CopyTo(raw, i + 24);
    }
    File.WriteAllBytes(path, raw);
    return path;
}
static string NameAt(byte[] bytes, int offset, int length) => offset + length <= bytes.Length ? Encoding.UTF8.GetString(bytes, offset, length) : "";
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static bool IsRunning(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
(string Directory, BundleManifest Manifest) CreateFakeRuntime(string name, bool exitAfterReady)
{
    var directory = Path.Combine(root, name); var runtime = Path.Combine(directory, "runtime");
    Directory.CreateDirectory(runtime); Directory.CreateDirectory(Path.Combine(directory, "models"));
    var assembly = typeof(Manifest).Assembly.Location;
    var assemblyDirectory = Path.GetDirectoryName(assembly)!;
    var assemblyName = Path.GetFileNameWithoutExtension(assembly);
    foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" }) File.Copy(Path.Combine(assemblyDirectory, assemblyName + extension), Path.Combine(runtime, assemblyName + extension));
    File.Copy(Path.Combine(assemblyDirectory, assemblyName + ".exe"), Path.Combine(runtime, "llama-server.exe"));
    File.WriteAllText(Path.Combine(directory, "models/model.gguf"), exitAfterReady ? "EXIT_AFTER_READY" : "STAY_RUNNING");
    return (directory, FakeManifest());
}
static BundleManifest FakeManifest() => new(1, "fake-runtime", DateTime.UtcNow.ToString("o"), new("Fake runtime", "models/model.gguf", new string('0', 64), 4096), new("runtime/llama-server.exe"), []);
static async Task RunFakeRuntime(string[] arguments)
{
    string ReadArg(string key) => arguments[Array.IndexOf(arguments, key) + 1];
    var port = int.Parse(ReadArg("--port"));
    var exits = File.ReadAllText(ReadArg("-m")).Contains("EXIT_AFTER_READY", StringComparison.Ordinal);
    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "pid.txt"), Environment.ProcessId.ToString());
    var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
    bool responseSent = false;
    try
    {
        using var safetyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            using var socket = await listener.AcceptTcpClientAsync(safetyDeadline.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            while (await reader.ReadLineAsync(safetyDeadline.Token) is { Length: > 0 }) { }
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 15\r\nConnection: close\r\n\r\n{\"status\":\"ok\"}");
            await stream.WriteAsync(response, safetyDeadline.Token);
            await stream.FlushAsync(safetyDeadline.Token);
            if (exits && !responseSent) { responseSent = true; _ = Task.Run(async () => { await Task.Delay(1200); Environment.Exit(17); }); }
        }
    }
    catch (OperationCanceledException) { }
    finally { listener.Stop(); }
}

record Payload(string Path, byte[] Bytes);
record Extra(string Path, string Content);
sealed class Manifest
{
    public int SchemaVersion { get; set; } = 1;
    public string BundleId { get; set; } = "fixture";
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public ManifestModel Model { get; set; } = new();
    public ManifestRuntime Runtime { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = [];
    public object[] Sources { get; set; } = [new { url = "https://publisher.invalid/pinned-model", revision = "fixed", license = "fixture-only" }];
}
sealed class ManifestModel { public string Name { get; set; } = ""; public string File { get; set; } = ""; public string Sha256 { get; set; } = ""; public int ContextSize { get; set; } }
sealed class ManifestRuntime { public string File { get; set; } = ""; }
sealed class ManifestFile { public string Path { get; set; } = ""; public long Size { get; set; } public string Sha256 { get; set; } = ""; }
