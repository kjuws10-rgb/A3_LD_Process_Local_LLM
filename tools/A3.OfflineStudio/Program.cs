using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using A3.OfflineStudio.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace A3.OfflineStudio;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception e)
        {
            if (args.Contains("--serve")) Console.Error.WriteLine(e.Message);
            else MessageBox.Show(e.Message, "A3 스튜디오 시작 오류");
            Environment.ExitCode = 1;
        }
    }
    private static void Run(string[] args)
    {
        string? Arg(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Arg("--state") is { } state) StudioSettings.StateRoot = Path.GetFullPath(state);
        if (Arg("--trace") is { } trace) StudioSettings.TracePath = Path.GetFullPath(trace);
        var settings = StudioSettings.Load();
        if (Arg("--repo") is { } repo) { settings.RepositoryPath = Path.GetFullPath(repo); settings.Save(); }
        var port = int.TryParse(Arg("--port"), out var fixedPort) ? fixedPort : ModelHost.AvailablePort();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var url = $"http://127.0.0.1:{port}/?token={token}";
        using var mutex = new Mutex(true, "Local\\A3OfflineStudio-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(StudioSettings.StateRoot)))[..16], out var first);
        if (!first) { var session = Path.Combine(StudioSettings.StateRoot, "session.json"); if (File.Exists(session)) { using var json = JsonDocument.Parse(File.ReadAllText(session)); Process.Start(new ProcessStartInfo(json.RootElement.GetProperty("url").GetString()!) { UseShellExecute = true }); } return; }
        if (args.Contains("--serve")) { RunServer(settings, port, token, null, importBundle: Arg("--import-bundle")).GetAwaiter().GetResult(); return; }
        ApplicationConfiguration.Initialize();
        using var shell = new StudioShell(url);
        shell.Shown += async (_, _) =>
        {
            shell.Hide();
            try { await RunServer(settings, port, token, shell, () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })); }
            catch (Exception e) { MessageBox.Show(e.Message, "A3 스튜디오 시작 오류"); Application.Exit(); }
        };
        Application.Run(shell);
    }

    private static async Task RunServer(StudioSettings settings, int port, string token, StudioShell? shell, Action? onReady = null, string? importBundle = null)
    {
        using var state = new StudioState(settings);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        await using var app = builder.Build();
        if (shell is not null) Application.ApplicationExit += (_, _) => { state.Dispose(); app.Lifetime.StopApplication(); };
        var origin = $"http://127.0.0.1:{port}";
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (context.Request.Host.Value != $"127.0.0.1:{port}" || (context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin.ToString() != origin)) { context.Response.StatusCode = 403; return; }
            var supplied = context.Request.Path == "/" ? context.Request.Query["token"].FirstOrDefault() ?? context.Request.Cookies["studio"] ?? "" : context.Request.Headers["X-Studio-Token"].ToString();
            if (supplied.Length != token.Length || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(token))) { context.Response.StatusCode = 403; return; }
            if (context.Request.Path == "/") context.Response.Cookies.Append("studio", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
            if (context.Request.ContentLength > 1_000_000) { context.Response.StatusCode = 413; return; }
            try { await next(context); }
            catch (OperationCanceledException) { if (!context.Response.HasStarted) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "작업을 중지했습니다." }); } }
            catch (Exception e) { if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); } }
        });
        app.MapGet("/", async () =>
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("A3.OfflineStudio.Web.index.html")!;
            using var reader = new StreamReader(stream);
            return Results.Content((await reader.ReadToEndAsync()).Replace("__STUDIO_TOKEN__", token), "text/html; charset=utf-8");
        });
        app.MapGet("/api/status", () => Results.Json(state.Snapshot()));
        app.MapPost("/api/repository", async () =>
        {
            if (shell is null) throw new InvalidOperationException("프로젝트는 --repo 옵션으로 지정하세요.");
            var path = await shell.ChooseFolder();
            if (path is not null) await state.SetRepository(path);
            return Results.Json(state.Snapshot());
        });
        app.MapPost("/api/import-document", async () =>
        {
            if (shell is null) throw new InvalidOperationException("데스크톱 실행에서 자료를 추가하세요.");
            var paths = await shell.ChooseDocuments();
            if (paths.Length > 0) await state.ImportDocuments(paths);
            return Results.Json(state.Snapshot());
        });
        app.MapPost("/api/import-bundle", async () =>
        {
            if (shell is null) throw new InvalidOperationException("데스크톱 실행에서 업데이트를 가져오세요.");
            var path = await shell.ChooseFile("A3 업데이트 묶음|*.zip");
            if (path is not null) state.StartBundleImport(path);
            return Results.Json(state.Snapshot());
        });
        app.MapPost("/api/rollback-model", async () => { await state.RollbackModel(); return Results.Json(state.Snapshot()); });
        app.MapPost("/api/reindex", async () => { await state.Reindex(); return Results.Json(state.Snapshot()); });
        app.MapPost("/api/chat", async (ChatRequest request, HttpContext context) => Results.Json(await state.Chat(request.Text, context.RequestAborted)));
        app.MapPost("/api/cancel", () => { state.Cancel(); return Results.Ok(); });
        app.MapPost("/api/reset", () => { state.Reset(); return Results.Ok(); });
        app.MapPost("/api/apply", async (IdRequest r) => { await state.Apply(r.Id, false); return Results.Json(state.Snapshot()); });
        app.MapPost("/api/undo", async (IdRequest r) => { await state.Apply(r.Id, true); return Results.Json(state.Snapshot()); });
        app.MapPost("/api/discard", async (IdRequest r) => { await state.Discard(r.Id); return Results.Json(state.Snapshot()); });
        app.MapGet("/api/proposals", () => Results.Json(state.Proposals()));
        app.MapPost("/api/build", async () => Results.Json(new { text = await state.Build() }));
        app.MapPost("/api/shutdown", () => { state.Cancel(); app.Lifetime.StopApplication(); shell?.BeginInvoke(() => Application.Exit()); return Results.Ok(); });
        await app.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(StudioSettings.StateRoot, "session.json"), JsonSerializer.Serialize(new { url = origin + "/?token=" + token, pid = Environment.ProcessId }, StudioSettings.Json));
        onReady?.Invoke();
        _ = Task.Run(async () => { await state.Initialize(); if (importBundle is not null) { try { await state.ImportBundle(importBundle); } catch { } } });
        await app.WaitForShutdownAsync();
        File.Delete(Path.Combine(StudioSettings.StateRoot, "session.json"));
    }
    private sealed record ChatRequest(string Text);
    private sealed record IdRequest(string Id);
}

internal sealed class StudioShell : Form
{
    private readonly NotifyIcon tray;
    public StudioShell(string url)
    {
        Text = "A3 오프라인 스튜디오"; ShowInTaskbar = false; WindowState = FormWindowState.Minimized;
        var menu = new ContextMenuStrip();
        void Open() => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        menu.Items.Add("스튜디오 열기", null, (_, _) => Open());
        menu.Items.Add("종료", null, (_, _) => Application.Exit());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "A3 오프라인 스튜디오", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Open();
    }
    public Task<string?> ChooseFolder() => Dialog(() => { using var d = new FolderBrowserDialog { Description = "A3_LD_Process_SW_IPS 저장소 폴더 선택", UseDescriptionForTitle = true }; return d.ShowDialog(this) == DialogResult.OK ? d.SelectedPath : null; });
    public Task<string?> ChooseFile(string filter) => Dialog(() => { using var d = new OpenFileDialog { Filter = filter, CheckFileExists = true }; return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null; });
    public Task<string[]> ChooseDocuments()
    {
        var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            try { using var d = new OpenFileDialog { Title = "관련 자료 선택 · 여러 파일을 함께 선택할 수 있습니다", Filter = "관련 자료|*.md;*.txt;*.csv;*.json;*.xml;*.cs;*.xaml;*.docx;*.xlsx", CheckFileExists = true, Multiselect = true }; completion.SetResult(d.ShowDialog(this) == DialogResult.OK ? d.FileNames : []); }
            catch (Exception e) { completion.SetException(e); }
        });
        return completion.Task;
    }
    private Task<string?> Dialog(Func<string?> action)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() => { try { completion.SetResult(action()); } catch (Exception e) { completion.SetException(e); } });
        return completion.Task;
    }
    protected override void Dispose(bool disposing) { if (disposing) tray.Dispose(); base.Dispose(disposing); }
}
