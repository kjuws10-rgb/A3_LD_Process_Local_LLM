using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A3.OfflineStudio.Core;

if (args.Length == 2 && args[0] == "--benchmark")
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var repositoryIndex = new ProjectIndex(new RepositoryGuard(args[1]));
    await repositoryIndex.RefreshAsync();
    Console.WriteLine($"INDEX {repositoryIndex.FileCount} files / {repositoryIndex.ChunkCount} chunks in {watch.Elapsed.TotalSeconds:F2}s");
    watch.Restart();
    await repositoryIndex.RefreshAsync();
    Console.WriteLine($"REFRESH unchanged in {watch.Elapsed.TotalSeconds:F2}s");
    watch.Restart();
    var results = repositoryIndex.Search("DoReady ProcessReady 제어", 3, 2000);
    Console.WriteLine($"SEARCH {results.Count} citations in {watch.Elapsed.TotalSeconds:F2}s");
    return 0;
}

if (args.Length == 2 && args[0] == "--real-agent")
{
    string port = Environment.GetEnvironmentVariable("A3_REAL_PORT") ?? throw new InvalidOperationException("Local test port missing");
    string key = Environment.GetEnvironmentVariable("A3_REAL_KEY") ?? throw new InvalidOperationException("Local test authentication missing");
    string state = Path.Combine(Path.GetTempPath(), "A3OfflineStudioRealTest-" + Guid.NewGuid().ToString("N"));
    try
    {
        var guard = new RepositoryGuard(args[1]);
        using var engine = new AgentEngine(new ProjectIndex(guard), new ChangeStore(guard, state), new Uri($"http://127.0.0.1:{port}/v1/chat/completions"), key, "a3-local");
        engine.Trace += (round, response) => Console.WriteLine($"ROUND {round}: {response}");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var reply = await engine.ChatAsync("README.md 파일의 내용을 읽고, 이 프로젝트의 용도를 한국어 두 문장으로 설명해줘. 코드 수정은 필요 없어.");
        Console.WriteLine($"FINAL ({watch.Elapsed.TotalSeconds:F2}s): {reply.Text}\nCITATIONS={reply.Citations.Count}; PROPOSALS={reply.Proposals.Count}");
    }
    finally
    {
        if (!Path.GetFullPath(state).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(state).StartsWith("A3OfflineStudioRealTest-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path");
        if (Directory.Exists(state)) Directory.Delete(state, true);
    }
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>
{
    ("dedicated repository + worktree verification", TestRepository),
    ("traversal / git metadata / private paths blocked", TestGuard),
    ("tracked retrieval / Korean / citations / freshness", TestIndex),
    ("explicit document approval only", TestImport),
    ("same-name document import supersedes active version", TestImportSupersedes),
    ("user-approved new source registration persists", TestRegisteredSource),
    ("proposal preview / apply / byte-exact undo", TestApplyUndo),
    ("stale multi-edit leaves all files untouched", TestStale),
    ("new-file collision blocked", TestNewFile),
    ("undo conflict leaves all files untouched", TestUndoConflict),
    ("UTF8 BOM / UTF16 / CP949 and newlines preserved", TestEncoding),
    ("partial filesystem failure rolls back prior writes", TestPartialFailure),
    ("local HTTP tools / auth / citations / no auto apply", TestAgent),
    ("endpoint restriction / bounded unknown tools", TestAgentBoundary),
    ("repeated tools force final with original request and evidence", TestForcedFinal),
    ("explicit file read-only request answers from that source", TestExplicitReadOnly),
    ("failed proposal may retry after evidence read", TestProposalRetry),
    ("model request cancellation", TestCancellation)
};
int failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed");
return failed == 0 ? 0 : 1;

static Task TestRepository()
{
    using var fixture = new Fixture();
    fixture.Guard.VerifyRepository();
    Check(!RepositoryGuard.IsDedicatedRemote("https://github.com/kjuws10-rgb/A3_LD_Process_SW_IPS.evil.git"), "remote suffix accepted");
    Check(!RepositoryGuard.IsDedicatedRemote("https://evil.example/kjuws10-rgb/A3_LD_Process_SW_IPS"), "foreign remote accepted");
    Check(RepositoryGuard.IsDedicatedRemote("git@github.com:kjuws10-rgb/A3_LD_Process_SW_IPS.git"), "SSH remote rejected");
    string actualGit = Path.Combine(fixture.Container, "metadata");
    Directory.Move(Path.Combine(fixture.Root, ".git"), actualGit);
    File.WriteAllText(Path.Combine(fixture.Root, ".git"), "gitdir: " + actualGit);
    fixture.Guard.VerifyRepository();
    File.WriteAllText(Path.Combine(actualGit, "config"), "[remote \"origin\"]\n url = https://github.com/other/repo.git\n");
    Throws<InvalidOperationException>(() => fixture.Guard.VerifyRepository());
    return Task.CompletedTask;
}

static Task TestGuard()
{
    using var fixture = new Fixture();
    string[] denied = ["../outside.txt", "a/../../outside.txt", ".git/config", "folder/.git/config", "data/log.txt", "bin/app.cs", "runtime/model.json", "logs/out.txt", ".env", ".env.production", "secrets/key.txt", "my-api_key.txt", "approved-docs/.approval.json", "file.cs:stream", "C:\\Windows\\win.ini", "https://example.com/x", "x/./file.cs", "x./file.cs", "x /file.cs", "CON.txt", "NUL", "x/COM1.cs"];
    foreach (var path in denied) Throws<UnauthorizedAccessException>(() => fixture.Guard.ResolvePath(path));
    Check(fixture.Guard.ResolvePath("src/Test.cs", true).StartsWith(fixture.Root), "valid path outside root");
    string outside = Path.Combine(fixture.Container, "outside");
    Directory.CreateDirectory(outside);
    string junction = Path.Combine(fixture.Root, "linked");
    try
    {
        Directory.CreateSymbolicLink(junction, outside);
        Throws<UnauthorizedAccessException>(() => fixture.Guard.ResolvePath("linked/test.txt"));
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { Console.WriteLine("  INFO OS denied symlink creation; symlink branch not exercised"); }
    return Task.CompletedTask;
}

static async Task TestIndex()
{
    using var fixture = new Fixture();
    fixture.Write("Demo.cs", "class Demo { void ProcessReady() { } }\n// 레이저 보정 절차\n");
    fixture.Write("untracked.txt", "UNTRACKED-PRIVATE-DATA");
    fixture.Write("credentials.json", "{\"apiKey\":\"do-not-send-me\"}");
    fixture.Write("private.config", "password = \"secretvalue\"");
    fixture.Track("Demo.cs", "credentials.json", "private.config");
    var index = new ProjectIndex(fixture.Guard);
    await index.RefreshAsync();
    Check(index.FileCount == 1, "nontracked/private file indexed");
    var hits = index.Search("레이저 보정", 3);
    Check(hits.Count == 1 && hits[0].Path == "Demo.cs" && hits[0].StartLine == 1 && hits[0].Sha256 == Hash(fixture.ReadBytes("Demo.cs")), "wrong citation");
    Check(hits[0].Citation.Contains("sha256:"), "citation SHA missing");
    fixture.Write("Demo.cs", "class Demo { void ProcessReady() { /* CURRENT */ } }\n");
    var fresh = index.Search("ProcessReady");
    Check(fresh.Count > 0 && fresh[0].Text.Contains("CURRENT") && fresh[0].Sha256 == Hash(fixture.ReadBytes("Demo.cs")), "stale contents returned");
    Check(index.Search("ProcessReady에서").Count > 0, "Korean suffix broke symbol tokenization");
    Throws<UnauthorizedAccessException>(() => index.Read("untracked.txt"));
    Throws<UnauthorizedAccessException>(() => index.Read(".git/config"));
}

static async Task TestImport()
{
    using var fixture = new Fixture();
    var index = new ProjectIndex(fixture.Guard);
    string external = Path.Combine(fixture.Container, "사용자자료.txt");
    File.WriteAllText(external, "가공 시나리오 단계 설명입니다.");
    string path = index.ImportDocument(external);
    fixture.Write("approved-docs/not-approved.txt", "must not index");
    await index.RefreshAsync();
    Check(index.FileCount == 1 && index.Read(path).Text.Contains("가공"), "approved doc unavailable");
    Throws<UnauthorizedAccessException>(() => index.Read("approved-docs/not-approved.txt"));
    File.WriteAllText(external, "api_key=\"sensitive-key-value\"");
    Throws<UnauthorizedAccessException>(() => index.ImportDocument(external));
}

static async Task TestRegisteredSource()
{
    using var fixture = new Fixture();
    fixture.Write("New.cs", "class UserApprovedSource {}\n");
    var index = new ProjectIndex(fixture.Guard);
    await index.RefreshAsync();
    Check(index.Search("UserApprovedSource").Count == 0, "untracked source implicitly indexed");
    index.RegisterProjectFile("New.cs");
    index = new ProjectIndex(fixture.Guard);
    await index.RefreshAsync();
    Check(index.Search("UserApprovedSource").Count == 1, "registered source unavailable after restart");
    Throws<UnauthorizedAccessException>(() => index.Read(".offline-studio/project-files.json"));
}

static async Task TestImportSupersedes()
{
    using var fixture = new Fixture();
    var index = new ProjectIndex(fixture.Guard);
    string source = Path.Combine(fixture.Container, "절차서.txt");
    File.WriteAllText(source, "OLD_SUPERSEDED_DECISION");
    string previous = index.ImportDocument(source);
    File.WriteAllText(source, "CURRENT_APPROVED_DECISION");
    string current = index.ImportDocument(source);
    string another = Path.Combine(fixture.Container, "다른절차서.txt");
    File.WriteAllText(another, "OTHER_RELATED_DOCUMENT");
    index.ImportDocument(another);
    await index.RefreshAsync();
    Check(index.FileCount == 2 && index.Search("CURRENT_APPROVED_DECISION").Count > 0, "latest document missing");
    Check(index.Search("OLD_SUPERSEDED_DECISION").All(h => !h.Text.Contains("OLD_SUPERSEDED_DECISION")) && File.Exists(fixture.Full(previous)), "old import not superseded/preserved");
    Throws<UnauthorizedAccessException>(() => index.Read(previous));
    Check(index.Read(current).Text.Contains("CURRENT"), "current import unavailable");
}

static Task TestApplyUndo()
{
    using var fixture = new Fixture();
    fixture.Write("Demo.cs", "before\r\n");
    var store = fixture.Store();
    var proposal = store.Propose("개발 검토", [new("Demo.cs", Hash(fixture.ReadBytes("Demo.cs")), "after\n"), new("src/New.cs", "", "class New {}\n")]);
    Check(fixture.Read("Demo.cs") == "before\r\n" && !File.Exists(fixture.Full("src/New.cs")), "proposal applied itself");
    Check(proposal.Preview.Contains("-before") && proposal.Preview.Contains("+after"), "preview not meaningful");
    Check(store.ListPending().Count == 1, "pending missing");
    Check(store.Apply(proposal.Id).Status == "applied", "apply status wrong");
    Check(store.ListHistory().Any(p => p.Id == proposal.Id && p.Status == "applied"), "applied history missing");
    Check(fixture.Read("Demo.cs") == "after\r\n" && File.Exists(fixture.Full("src/New.cs")), "apply failed");
    var reopened = fixture.Store();
    Check(reopened.Undo(proposal.Id).Status == "undone", "undo status wrong");
    Check(fixture.Read("Demo.cs") == "before\r\n" && !File.Exists(fixture.Full("src/New.cs")), "undo failed");
    return Task.CompletedTask;
}

static Task TestStale()
{
    using var fixture = new Fixture();
    fixture.Write("A.cs", "A"); fixture.Write("B.cs", "B");
    var store = fixture.Store();
    var proposal = store.Propose("batch", [new("A.cs", Hash(fixture.ReadBytes("A.cs")), "new A"), new("B.cs", Hash(fixture.ReadBytes("B.cs")), "new B")]);
    fixture.Write("B.cs", "concurrent edit");
    Throws<InvalidOperationException>(() => store.Apply(proposal.Id));
    Check(fixture.Read("A.cs") == "A" && fixture.Read("B.cs") == "concurrent edit", "stale batch partially applied");
    return Task.CompletedTask;
}

static Task TestNewFile()
{
    using var fixture = new Fixture();
    fixture.Write("A.cs", "A");
    var store = fixture.Store();
    Throws<InvalidOperationException>(() => store.Propose("bad", [new("A.cs", "", "replacement")]));
    var proposal = store.Propose("new", [new("New.cs", "", "new")]);
    fixture.Write("New.cs", "someone else");
    Throws<InvalidOperationException>(() => store.Apply(proposal.Id));
    Check(fixture.Read("New.cs") == "someone else", "new file overwritten");
    return Task.CompletedTask;
}

static Task TestUndoConflict()
{
    using var fixture = new Fixture();
    fixture.Write("A.cs", "A"); fixture.Write("B.cs", "B");
    var store = fixture.Store();
    var proposal = store.Propose("batch", [new("A.cs", Hash(fixture.ReadBytes("A.cs")), "new A"), new("B.cs", Hash(fixture.ReadBytes("B.cs")), "new B")]);
    store.Apply(proposal.Id);
    fixture.Write("B.cs", "user edit");
    Throws<InvalidOperationException>(() => store.Undo(proposal.Id));
    Check(fixture.Read("A.cs") == "new A" && fixture.Read("B.cs") == "user edit", "undo clobbered user work");
    return Task.CompletedTask;
}

static Task TestEncoding()
{
    using var fixture = new Fixture();
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    Encoding[] encodings = [new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)];
    var store = fixture.Store();
    for (int i = 0; i < encodings.Length; i++)
    {
        string path = $"Encoding{i}.cs";
        var encoding = encodings[i];
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes("// 한글 원본\r\n")];
        fixture.WriteBytes(path, original);
        var proposal = store.Propose("인코딩", [new(path, Hash(original), "// 한글 변경\n")]);
        store.Apply(proposal.Id);
        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("// 한글 변경\r\n")];
        Check(fixture.ReadBytes(path).SequenceEqual(expected), "encoding not preserved " + encoding.WebName);
        store.Undo(proposal.Id);
        Check(fixture.ReadBytes(path).SequenceEqual(original), "undo not byte-exact " + encoding.WebName);
    }
    return Task.CompletedTask;
}

static Task TestPartialFailure()
{
    using var fixture = new Fixture();
    fixture.Write("A.cs", "original");
    var store = fixture.Store();
    var proposal = store.Propose("filesystem failure", [new("A.cs", Hash(fixture.ReadBytes("A.cs")), "after"), new("blocked/New.cs", "", "new")]);
    fixture.Write("blocked", "file blocks directory");
    Throws<IOException>(() => store.Apply(proposal.Id));
    Check(fixture.Read("A.cs") == "original" && fixture.Read("blocked") == "file blocks directory", "partial failure not rolled back");
    Check(store.Get(proposal.Id).Status == "failed", "failed transaction still pending");
    return Task.CompletedTask;
}

static async Task TestAgent()
{
    using var fixture = new Fixture();
    fixture.Write("Demo.cs", "class Demo { int Value = 1; }\n"); fixture.Track("Demo.cs");
    var index = new ProjectIndex(fixture.Guard);
    var store = fixture.Store();
    int calls = 0;
    using var http = new HttpClient(new MockHandler(async (request, cancellationToken) =>
    {
        Check(request.RequestUri?.Host == "127.0.0.1", "external request");
        Check(request.Headers.Authorization?.ToString() == "Bearer local-secret", "auth missing");
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        int chars = payload.RootElement.GetProperty("messages").EnumerateArray().Sum(m => m.GetProperty("content").GetString()!.Length);
        Check(chars <= 6200, "context over budget");
        Check(payload.RootElement.GetProperty("max_tokens").GetInt32() == 1000, "output tokens unbounded");
        string content = calls++ switch
        {
            0 => JsonSerializer.Serialize(new { action = "read", path = "Demo.cs", startLine = 1, maxLines = 40 }),
            1 => JsonSerializer.Serialize(new { action = "propose", title = "Value 변경", edits = new[] { new { path = "Demo.cs", baseSha256 = Hash(fixture.ReadBytes("Demo.cs")), find = "int Value = 1;", replace = "int Value = 2;" } } }),
            _ => JsonSerializer.Serialize(new { action = "final", message = "Value를 2로 바꾸는 제안입니다. [Demo.cs:1-1] 검증이 필요합니다." })
        };
        return Llm(content);
    }));
    using var agent = new AgentEngine(index, store, new Uri("http://127.0.0.1:18080/v1/chat/completions"), "local-secret", "a3-local", http);
    var reply = await agent.ChatAsync("Demo Value를 2로 변경해줘");
    Check(reply.Proposals.Count == 1 && reply.Citations.Count > 0 && reply.Text.Contains("미리보기") && calls == 2, "tool reply incomplete or redundant final inference");
    Check(fixture.Read("Demo.cs").Contains("Value = 1"), "agent auto applied changes");
    store.Apply(reply.Proposals[0].Id);
    Check(fixture.Read("Demo.cs").Contains("Value = 2"), "proposed replacement incorrect");
}

static async Task TestAgentBoundary()
{
    using var fixture = new Fixture();
    fixture.Write("Demo.cs", "class Demo {}\n"); fixture.Track("Demo.cs");
    var index = new ProjectIndex(fixture.Guard); var store = fixture.Store();
    string[] bad = ["https://127.0.0.1:18080/v1/chat/completions", "http://localhost:18080/v1/chat/completions", "http://example.com:18080/v1/chat/completions", "http://127.0.0.1:18080/v1/chat/completions?redirect=x", "http://127.0.0.1:80/v1/chat/completions"];
    foreach (var url in bad) Throws<ArgumentException>(() => new AgentEngine(index, store, new Uri(url), "key", "model"));
    int calls = 0;
    using var http = new HttpClient(new MockHandler((_, _) => { calls++; return Task.FromResult(Llm("{\"action\":\"shell\",\"command\":\"malicious\"}")); }));
    using var agent = new AgentEngine(index, store, new Uri("http://127.0.0.1:18080/v1/chat/completions"), "key", "model", http);
    var reply = await agent.ChatAsync("무언가 실행해줘");
    Check(calls == 5 && reply.Proposals.Count == 0 && store.ListPending().Count == 0, "tool loop not bounded");
}

static async Task TestCancellation()
{
    using var fixture = new Fixture();
    fixture.Write("Demo.cs", "class Demo {}\n"); fixture.Track("Demo.cs");
    using var http = new HttpClient(new MockHandler(async (_, token) => { await Task.Delay(30_000, token); return Llm("{}"); }));
    using var agent = new AgentEngine(new ProjectIndex(fixture.Guard), fixture.Store(), new Uri("http://127.0.0.1:18080/v1/chat/completions"), "key", "model", http);
    using var cancel = new CancellationTokenSource(100);
    try { await agent.ChatAsync("Demo 분석", cancel.Token); throw new Exception("request was not cancelled"); }
    catch (OperationCanceledException) { }
}

static async Task TestForcedFinal()
{
    using var fixture = new Fixture();
    fixture.Write("README.md", "# A3_CURRENT_SOURCE\n가공 소프트웨어 설명입니다.\n"); fixture.Track("README.md");
    int calls = 0;
    int traces = 0;
    using var http = new HttpClient(new MockHandler(async (request, token) =>
    {
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var messages = payload.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()!).ToArray();
        Check(messages.Any(m => m.Contains("ORIGINAL_REQUEST_MARKER")), "original request lost");
        Check(messages.Sum(m => m.Length) <= 6200, "prompt budget exceeded");
        if (calls++ < 2) return Llm("{\"action\":\"read\",\"path\":\"README.md\",\"startLine\":1,\"maxLines\":40}");
        var schema = payload.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema");
        Check(schema.GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "final", "repetition did not constrain final action");
        Check(messages.Last().Contains("A3_CURRENT_SOURCE"), "final turn lost source evidence");
        return Llm("{\"action\":\"final\",\"message\":\"A3 가공 소프트웨어를 설명하는 문서입니다.\"}");
    }));
    using var agent = new AgentEngine(new ProjectIndex(fixture.Guard), fixture.Store(), new Uri("http://127.0.0.1:18080/v1/chat/completions"), "key", "model", http);
    agent.Trace += (_, _) => traces++;
    var reply = await agent.ChatAsync("ORIGINAL_REQUEST_MARKER README.md 읽고 두 문장 설명해줘. " + new string('가', 2200));
    Check(calls == 3 && traces == 3 && reply.Text.Contains("가공"), "forced final failed");
}

static async Task TestExplicitReadOnly()
{
    using var fixture = new Fixture();
    fixture.Write("README.md", "# Laser Drilling C# Conversion\nConvert operation flow to C# / WPF.\n");
    fixture.Write("Review.cs", "// 파일 내용 읽고 설명 코드 수정 프로젝트 자료\nclass Unrelated {}\n");
    fixture.Track("README.md", "Review.cs");
    int calls = 0;
    using var http = new HttpClient(new MockHandler(async (request, token) =>
    {
        calls++;
        using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var messages = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Check(messages.Length == 2 && messages[1].GetProperty("content").GetString()!.Contains("C# / WPF"), "explicit file evidence absent");
        Check(!messages[1].GetProperty("content").GetString()!.Contains("Unrelated"), "unrelated retrieval overrode explicit source");
        Check(payload.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("action").GetProperty("const").GetString() == "final", "read-only did not request final directly");
        return Llm("{\"action\":\"final\",\"message\":\"기존 가공 흐름을 C# / WPF로 변환하는 프로젝트입니다.\"}");
    }));
    using var engine = new AgentEngine(new ProjectIndex(fixture.Guard), fixture.Store(), new Uri("http://127.0.0.1:18080/v1/chat/completions"), "key", "model", http);
    var reply = await engine.ChatAsync("README.md에서 프로젝트 용도를 읽고 설명해줘. 코드 수정은 필요 없어.");
    Check(calls == 1 && reply.Citations.All(c => c.Path == "README.md") && reply.Proposals.Count == 0, "explicit read-only flow incorrect");
}

static async Task TestProposalRetry()
{
    using var fixture = new Fixture();
    fixture.Write("README.md", "# Original Heading\n본문을 보존합니다.\n"); fixture.Track("README.md");
    int calls = 0;
    using var http = new HttpClient(new MockHandler((_, _) =>
    {
        int round = calls++;
        if (round == 1) return Task.FromResult(Llm("{\"action\":\"read\",\"path\":\"README.md\",\"startLine\":1,\"maxLines\":40}"));
        Check(round < 3, "successful proposal caused another model request");
        return Task.FromResult(Llm(JsonSerializer.Serialize(new { action = "propose", title = "제목 교체", edits = new[] { new { path = "README.md", baseSha256 = Hash(fixture.ReadBytes("README.md")), find = "# Original Heading", replace = "# Updated Heading" } } })));
    }));
    using var engine = new AgentEngine(new ProjectIndex(fixture.Guard), fixture.Store(), new Uri("http://127.0.0.1:18080/v1/chat/completions"), "key", "model", http);
    // No filename in the request: the first proposal must fail until the model explicitly reads the file.
    var reply = await engine.ChatAsync("프로젝트 문서의 제목을 바꾸는 제안을 만들어줘.");
    Check(calls == 3 && reply.Proposals.Count == 1 && reply.Text.Contains("아직 적용하거나 검증하지 않았습니다"), "failed proposal retry or deterministic acknowledgement failed");
    Check(reply.Proposals[0].Edits[0].Content == "# Updated Heading\n본문을 보존합니다.\n" && fixture.Read("README.md").StartsWith("# Original"), "retry did not preserve source/body");
}

static HttpResponseMessage Llm(string content) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } }), Encoding.UTF8, "application/json") };
static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
}

sealed class Fixture : IDisposable
{
    public string Container { get; } = Path.Combine(Path.GetTempPath(), "A3OfflineStudioTests-" + Guid.NewGuid().ToString("N"));
    public string Root { get; }
    public RepositoryGuard Guard { get; }
    public Fixture()
    {
        Root = Path.Combine(Container, "repo");
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
        File.WriteAllText(Path.Combine(Root, "Drilling.sln"), "test solution");
        File.WriteAllText(Path.Combine(Root, ".git", "config"), "[core]\n repositoryformatversion = 0\n[remote \"origin\"]\n url = https://github.com/kjuws10-rgb/A3_LD_Process_SW_IPS.git\n");
        Guard = new RepositoryGuard(Root);
        Track();
    }
    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public void Write(string relative, string text) => WriteBytes(relative, new UTF8Encoding(false).GetBytes(text));
    public void WriteBytes(string relative, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!); File.WriteAllBytes(Full(relative), bytes); }
    public string Read(string relative) => File.ReadAllText(Full(relative));
    public byte[] ReadBytes(string relative) => File.ReadAllBytes(Full(relative));
    public ChangeStore Store() => new(Guard, Path.Combine(Container, "state"));
    public void Track(params string[] paths)
    {
        using var memory = new MemoryStream();
        memory.Write("DIRC"u8);
        WriteUInt(memory, 2); WriteUInt(memory, (uint)paths.Length);
        foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            long start = memory.Position;
            byte[] entry = new byte[62];
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(24, 4), 0x81A4);
            BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(60, 2), (ushort)Math.Min(4095, Encoding.UTF8.GetByteCount(path)));
            memory.Write(entry); memory.Write(Encoding.UTF8.GetBytes(path)); memory.WriteByte(0);
            while ((memory.Position - start) % 8 != 0) memory.WriteByte(0);
        }
        byte[] body = memory.ToArray(); memory.Write(SHA1.HashData(body));
        File.WriteAllBytes(Path.Combine(Root, ".git", "index"), memory.ToArray());
    }
    private static void WriteUInt(Stream stream, uint value) { Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(data, value); stream.Write(data); }
    public void Dispose()
    {
        var target = Path.GetFullPath(Container);
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("A3OfflineStudioTests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
