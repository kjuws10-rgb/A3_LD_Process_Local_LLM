using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using A3.OfflineStudio.Core;

namespace A3.OfflineStudio;

internal sealed class StudioState(StudioSettings settings) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly BundleManager bundles = new();
    private readonly ModelHost model = new();
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };
    private RepositoryGuard? guard;
    private ProjectIndex? index;
    private ChangeStore? changes;
    private AgentEngine? agent;
    private CancellationTokenSource? request;
    private string operation = "초기 준비 중";
    private string? error;
    private bool busy = true;
    private bool disposed;
    public object Snapshot() => new { repository = settings.RepositoryPath, model = model.ModelName, ready = model.Ready && index is not null, modelStatus = model.Status, operation, error, busy, canCancel = request is not null, indexedFiles = index?.FileCount ?? 0, indexedChunks = index?.ChunkCount ?? 0, lastIndexed = index?.LastRefreshedUtc, bundleProgress = bundles.Progress, canRollback = settings.PreviousBundle is not null, logs = model.LogTail(), offline = true };
    public object Proposals() => changes?.ListHistory() ?? (object)Array.Empty<object>();
    public async Task Initialize()
    {
        await gate.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(settings.RepositoryPath))
            {
                foreach (var initial in new[] { Path.Combine(AppContext.BaseDirectory, "Project"), AppContext.BaseDirectory, Environment.CurrentDirectory }.Where(Directory.Exists))
                    for (var d = new DirectoryInfo(initial); d is not null; d = d.Parent)
                        if (File.Exists(Path.Combine(d.FullName, "Drilling.sln"))) { settings.RepositoryPath ??= d.FullName; break; }
            }
            if (settings.RepositoryPath is not null) await ConfigureRepository(settings.RepositoryPath);
            if (settings.ActiveBundle is not null) await StartModel(settings.ActiveBundle);
            else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "model-update.zip")))
            {
                operation = "처음 실행: 동봉된 모델을 자동 설치합니다.";
                var id = await bundles.ImportAsync(Path.Combine(AppContext.BaseDirectory, "model-update.zip"));
                await StartModel(id); settings.ActiveBundle = id; settings.Save();
            }
            operation = index is null ? "프로젝트 폴더를 한 번 선택하세요." : model.Ready ? "요청을 입력하세요." : "외부망에서 준비한 모델 묶음을 가져오세요.";
        }
        catch (Exception e) { error = e.Message; operation = "설정을 확인하세요."; }
        finally { busy = false; gate.Release(); }
    }
    private async Task ConfigureRepository(string path)
    {
        var next = new RepositoryGuard(path); next.VerifyRepository();
        var nextIndex = new ProjectIndex(next);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(next.RootPath)))[..16];
        var projectState = Path.Combine(StudioSettings.StateRoot, "projects", key);
        var nextChanges = new ChangeStore(next, projectState);
        var context = Path.Combine(AppContext.BaseDirectory, "Context");
        var seedMarker = Path.Combine(projectState, "bundled-context.json");
        Dictionary<string, string> seeded;
        try { seeded = File.Exists(seedMarker) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(seedMarker)) ?? new() : new(); }
        catch (System.Text.Json.JsonException) { seeded = new(); }
        if (Directory.Exists(context))
        {
            foreach (var file in Directory.EnumerateFiles(context, "*.md").Take(20))
            {
                if (new FileInfo(file).Length > 1_048_576) throw new InvalidDataException("동봉 관련 자료가 1MB 제한을 초과합니다.");
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
                if (seeded.GetValueOrDefault(Path.GetFileName(file)) == hash) continue;
                nextIndex.ImportDocument(file); seeded[Path.GetFileName(file)] = hash;
            }
            await File.WriteAllTextAsync(seedMarker + ".new", System.Text.Json.JsonSerializer.Serialize(seeded));
            File.Move(seedMarker + ".new", seedMarker, true);
        }
        nextIndex.Progress += (current, total, path) => operation = current >= total ? "최신 코드 확인 완료 · AI가 검토 중입니다." : $"프로젝트 읽기 {current}/{total}: {path}";
        await nextIndex.RefreshAsync();
        guard = next; index = nextIndex; changes = nextChanges;
        settings.RepositoryPath = next.RootPath; settings.Save();
        ConfigureAgent();
    }
    private void ConfigureAgent()
    {
        agent?.Dispose();
        agent = index is not null && changes is not null && model.Ready ? new AgentEngine(index, changes, model.Endpoint, model.ApiKey, "a3-local", client) : null;
        if (agent is not null && StudioSettings.TracePath is { } trace) agent.Trace += (round, raw) => File.AppendAllText(trace, $"\nRound {round}\n{raw}\n");
    }
    private async Task StartModel(string id)
    {
        var (directory, manifest) = bundles.Read(id);
        operation = "로컬 모델을 준비하고 있습니다.";
        await model.StartAsync(directory, manifest);
        ConfigureAgent();
    }
    private async Task Locked(string title, Func<Task> work)
    {
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException("현재 작업이 끝난 뒤 다시 눌러주세요.");
        busy = true; error = null; operation = title;
        try { await work(); operation = "완료. 다음 요청을 입력하세요."; }
        catch (Exception e) { error = e.Message; operation = "작업을 완료하지 못했습니다."; throw; }
        finally { busy = false; gate.Release(); }
    }
    public Task SetRepository(string path) => Locked("프로젝트 확인·자료 갱신 중", () => ConfigureRepository(path));
    public Task Reindex() => Locked("최신 코드·자료를 읽는 중", async () => { RequireProject(); await index!.RefreshAsync(); });
    public Task ImportDocuments(IReadOnlyList<string> paths) => Locked("관련 자료 추가 중", async () =>
    {
        RequireProject();
        if (paths.Count > 100) throw new InvalidOperationException("관련 자료는 한 번에 100개까지 선택하세요.");
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx")
                {
                    var tempDirectory = Path.Combine(StudioSettings.StateRoot, "import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tempDirectory);
                    try { var text = DocumentExtractor.Extract(path); var temp = Path.Combine(tempDirectory, Path.GetFileName(path) + ".txt"); await File.WriteAllTextAsync(temp, text); index!.ImportDocument(temp); }
                    finally { Directory.Delete(tempDirectory, true); }
                }
                else index!.ImportDocument(path);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.Xml.XmlException) { failures.Add(Path.GetFileName(path) + ": " + e.Message); }
        }
        await index!.RefreshAsync();
        if (failures.Count > 0) throw new InvalidOperationException($"{paths.Count - failures.Count}개 자료 추가 완료, {failures.Count}개 제외:\n" + string.Join("\n", failures.Take(5)));
    });
    public void StartBundleImport(string path)
    {
        if (busy) throw new InvalidOperationException("현재 작업을 마친 뒤 업데이트하세요.");
        _ = ImportBundle(path).ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
    public Task ImportBundle(string path) => Locked("모델 업데이트 가져오는 중", async () =>
    {
        var id = await bundles.ImportAsync(path);
        var previous = settings.ActiveBundle;
        try { await StartModel(id); }
        catch
        {
            if (previous is not null) { try { await StartModel(previous); } catch { } }
            throw;
        }
        settings.PreviousBundle = previous; settings.ActiveBundle = id; settings.Save();
    });
    public Task RollbackModel() => Locked("이전 모델로 복원 중", async () =>
    {
        var previous = settings.PreviousBundle ?? throw new InvalidOperationException("이전 모델이 없습니다.");
        var current = settings.ActiveBundle;
        try { await StartModel(previous); }
        catch { if (current is not null) { try { await StartModel(current); } catch { } } throw; }
        settings.PreviousBundle = current; settings.ActiveBundle = previous; settings.Save();
    });
    public async Task<AgentReply> Chat(string text, CancellationToken abort)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8000) throw new InvalidOperationException("요청은 1~8,000자로 입력하세요.");
        AgentReply? reply = null;
        await Locked("최신 자료 검색·코드 검토 중 · CPU에서는 시간이 걸릴 수 있습니다.", async () =>
        {
            if (agent is null || !model.Ready) throw new InvalidOperationException("프로젝트와 모델 준비를 먼저 완료하세요. 모델이 종료되었으면 앱을 다시 실행하세요.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(abort); request = linked;
            try { reply = await agent.ChatAsync(text, linked.Token); }
            finally { request = null; }
        });
        return reply!;
    }
    public void Cancel() => request?.Cancel();
    public void Reset() { if (busy) throw new InvalidOperationException("현재 요청을 중지한 뒤 새 대화를 시작하세요."); agent?.ResetConversation(); }
    public Task Discard(string id) => Locked("수정 제안 제외 중", () => { RequireProject(); changes!.Discard(id); return Task.CompletedTask; });
    public Task Apply(string id, bool undo) => Locked(undo ? "변경 되돌리는 중" : "수정 내용 적용 중", async () =>
    {
        RequireProject();
        if (undo) changes!.Undo(id);
        else { var applied = changes!.Apply(id); foreach (var edit in applied.Edits) index!.RegisterProjectFile(edit.Path); }
        await index!.RefreshAsync();
    });
    public async Task<string> Build()
    {
        string result = "";
        await Locked("프로젝트 빌드 검사 중", async () =>
        {
            RequireProject();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            request = timeout;
            try
            {
                var toolchain = Path.Combine(AppContext.BaseDirectory, "toolchain");
                var portable = Path.Combine(toolchain, "dotnet.exe");
                var feed = Path.Combine(toolchain, "feed");
                bool bundled = File.Exists(portable);
                if (bundled)
                {
                    if (!Directory.Exists(feed)) throw new InvalidOperationException("동봉된 SDK의 오프라인 참조 패키지가 없습니다. 외부망에서 툴체인을 다시 준비하세요.");
                    operation = "동봉된 패키지로 오프라인 빌드 준비 중";
                    var restored = await BuildStage(portable, toolchain, ["restore", "Drilling.sln", "--source", feed, "--packages", Path.Combine(StudioSettings.StateRoot, "build-packages"), "-p:NuGetAudit=false", "-p:RestoreIgnoreFailedSources=true", "-v:minimal"], timeout.Token);
                    result += restored.Text;
                    if (restored.Code != 0) { result = "오프라인 참조 패키지 복원 실패\n" + result; return; }
                }
                operation = "소스 빌드 검사 중 · 장비 앱을 실행하지 않습니다.";
                var built = await BuildStage(bundled ? portable : "dotnet", bundled ? toolchain : null, ["build", "Drilling.sln", "--no-restore", "--disable-build-servers", "-p:NuGetAudit=false", "-v:minimal"], timeout.Token);
                result += $"종료 코드 {built.Code}\n" + built.Text;
            }
            finally { request = null; }
            if (result.Length > 40000) result = result[..40000] + "\n… 이후 로그 생략";
        });
        return result;
    }
    private async Task<(int Code, string Text)> BuildStage(string executable, string? sdkRoot, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = guard!.RootPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"; info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1"; info.Environment["MSBuildEnableWorkloadResolver"] = "false";
        info.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false"; info.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["NUGET_CERT_REVOCATION_MODE"] = "offline"; info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        if (sdkRoot is not null) { info.Environment["DOTNET_ROOT"] = sdkRoot; info.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"; info.Environment["DOTNET_CLI_HOME"] = Path.Combine(StudioSettings.StateRoot, "dotnet-home"); }
        using var process = Process.Start(info) ?? throw new IOException(".NET SDK를 찾을 수 없습니다.");
        using var job = ProcessJob.Attach(process);
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
    private void RequireProject() { if (index is null || guard is null || changes is null) throw new InvalidOperationException("프로젝트 폴더를 먼저 선택하세요."); }
    public void Dispose() { if (disposed) return; disposed = true; Cancel(); model.Dispose(); client.Dispose(); }
}
