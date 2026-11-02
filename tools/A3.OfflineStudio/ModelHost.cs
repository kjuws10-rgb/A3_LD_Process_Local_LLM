using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace A3.OfflineStudio;

public sealed class ModelHost : IDisposable
{
    private Process? process;
    private ProcessJob? job;
    private bool stopping;
    private readonly Queue<string> log = new();
    private readonly HttpClient health = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
    public int Port { get; private set; }
    public string ApiKey { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private volatile bool ready;
    public bool Ready
    {
        get { if (!ready) return false; try { return process is { HasExited: false }; } catch (InvalidOperationException) { return false; } }
        private set => ready = value;
    }
    public string Status { get; private set; } = "모델 묶음이 필요합니다.";
    public string ModelName { get; private set; } = "미설치";
    public Uri Endpoint => new($"http://127.0.0.1:{Port}/v1/chat/completions");
    public static int AvailablePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }
    public string[] LogTail() { lock (log) return log.TakeLast(8).ToArray(); }
    public async Task StartAsync(string directory, BundleManifest m, CancellationToken ct = default)
    {
        Stop();
        Port = AvailablePort();
        ModelName = m.Model.Name;
        Status = "모델을 메모리에 불러오는 중입니다. 첫 실행은 잠시 걸립니다.";
        var exe = Path.GetFullPath(Path.Combine(directory, m.Runtime.File));
        var model = Path.GetFullPath(Path.Combine(directory, m.Model.File));
        var info = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var k in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || k.Contains("PROXY", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(k);
        string[] args = ["-m", model, "--host", "127.0.0.1", "--port", Port.ToString(), "--ctx-size", m.Model.ContextSize.ToString(), "--parallel", "1", "--threads", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(), "--n-gpu-layers", "0", "--offline", "--no-webui", "--no-agent", "--alias", "a3-local", "--api-key", ApiKey];
        foreach (var a in args) info.ArgumentList.Add(a);
        process = Process.Start(info) ?? throw new IOException("모델 실행기를 시작할 수 없습니다.");
        try { job = ProcessJob.Attach(process); }
        catch { try { process.Kill(true); } catch { } process.Dispose(); process = null; throw; }
        stopping = false;
        var started = process;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => { if (!stopping && ReferenceEquals(process, started)) { Ready = false; Status = "모델 실행기가 종료되었습니다. 앱을 다시 실행하거나 모델 묶음을 다시 가져오세요."; } };
        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("모델 실행기가 종료되었습니다. " + string.Join(" ", LogTail().TakeLast(3)));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/health");
                    request.Headers.Authorization = new("Bearer", ApiKey);
                    using var response = await health.SendAsync(request, deadline.Token);
                    if (response.IsSuccessStatusCode) { Ready = true; Status = "오프라인 모델 준비 완료"; return; }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(500, deadline.Token);
            }
        }
        catch { Stop(); Status = "모델 시작에 실패했습니다. 진단 내용을 확인하세요."; throw; }
    }
    private void Add(string? value) { if (value is null) return; lock (log) { log.Enqueue(value.Replace(ApiKey, "[local-key]")); while (log.Count > 80) log.Dequeue(); } }
    public void Stop() { stopping = true; Ready = false; job?.Dispose(); job = null; if (process is not null) { try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); } } catch (InvalidOperationException) { } process.Dispose(); process = null; } }
    public void Dispose() { Stop(); health.Dispose(); }
}
