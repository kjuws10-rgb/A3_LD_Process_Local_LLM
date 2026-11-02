using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace A3.OfflineStudio;

public sealed record BundleFile(string Path, long Size, string Sha256);
public sealed record BundleModel(string Name, string File, string Sha256, int ContextSize = 4096);
public sealed record BundleRuntime(string File);
public sealed record BundleManifest(int SchemaVersion, string BundleId, string CreatedUtc, BundleModel Model, BundleRuntime Runtime, List<BundleFile> Files, JsonElement? Sources = null);

public sealed class BundleManager
{
    public string BundlesRoot { get; } = Path.Combine(StudioSettings.StateRoot, "bundles");
    public string Progress { get; private set; } = "모델 업데이트 묶음을 가져오세요.";
    private static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') || relative.StartsWith('/') || relative.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' ')))
            throw new InvalidDataException("업데이트에 허용되지 않는 경로가 있습니다.");
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("업데이트 경로가 범위를 벗어났습니다.");
        return result;
    }
    public (string Directory, BundleManifest Manifest) Read(string bundle)
    {
        var dir = SafePath(BundlesRoot, bundle);
        return (dir, JsonSerializer.Deserialize<BundleManifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")), StudioSettings.Json) ?? throw new InvalidDataException("업데이트 정보가 비어 있습니다."));
    }
    public async Task<string> ImportAsync(string zipPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(BundlesRoot);
        var stage = Path.Combine(BundlesRoot, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count is < 3 or > 1500) throw new InvalidDataException("업데이트 파일 수가 올바르지 않습니다.");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                _ = SafePath(stage, e.FullName);
                if (((e.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (e.ExternalAttributes & 0x400) != 0) throw new InvalidDataException("링크 파일은 반입할 수 없습니다.");
                if (!entries.TryAdd(e.FullName, e)) throw new InvalidDataException("중복 업데이트 경로입니다.");
                total = checked(total + e.Length);
                if (total > 24L * 1024 * 1024 * 1024 || e.Length > 18L * 1024 * 1024 * 1024) throw new InvalidDataException("업데이트 크기 제한을 초과했습니다.");
            }
            if (!entries.TryGetValue("manifest.json", out var me) || me.Length > 2_000_000) throw new InvalidDataException("manifest.json이 없거나 너무 큽니다.");
            BundleManifest m;
            using (var stream = me.Open()) m = await JsonSerializer.DeserializeAsync<BundleManifest>(stream, StudioSettings.Json, ct) ?? throw new InvalidDataException("업데이트 정보가 비어 있습니다.");
            if (m.SchemaVersion != 1 || m.Model is null || m.Runtime is null || m.Files is null || m.Files.Count == 0 || m.Model.ContextSize is < 2048 or > 16384) throw new InvalidDataException("지원하지 않는 업데이트 형식입니다.");
            if (m.Runtime.File != "runtime/llama-server.exe" || !m.Model.File.StartsWith("models/", StringComparison.Ordinal) || !m.Model.File.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("모델 또는 실행기 위치가 잘못되었습니다.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in m.Files)
            {
                _ = SafePath(stage, f.Path);
                if (f.Path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || !names.Add(f.Path) || !entries.TryGetValue(f.Path, out var entry) || entry.Length != f.Size || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("목록과 실제 업데이트 파일이 일치하지 않습니다.");
            }
            if (!names.Contains(m.Model.File) || !names.Contains(m.Runtime.File) || entries.Count != names.Count + 1 || !m.Files.Single(f => f.Path.Equals(m.Model.File, StringComparison.OrdinalIgnoreCase)).Sha256.Equals(m.Model.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("모델 해시 또는 파일 목록이 불완전합니다.");
            var free = new DriveInfo(Path.GetPathRoot(stage)!).AvailableFreeSpace;
            if (free < total + 256L * 1024 * 1024) throw new IOException("업데이트를 풀 디스크 공간이 부족합니다.");
            var count = 0;
            foreach (var f in m.Files)
            {
                ct.ThrowIfCancellationRequested();
                Progress = $"업데이트 검증 {++count}/{m.Files.Count}: {f.Path}";
                var dest = SafePath(stage, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var source = entries[f.Path].Open())
                await using (var target = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024]; long copied = 0; int bytes;
                    while ((bytes = await source.ReadAsync(buffer, ct)) != 0)
                    {
                        copied += bytes;
                        if (copied > f.Size) throw new InvalidDataException("업데이트의 실제 크기가 목록을 초과했습니다.");
                        hasher.AppendData(buffer, 0, bytes);
                        await target.WriteAsync(buffer.AsMemory(0, bytes), ct);
                        if (f.Size > 100 * 1024 * 1024) Progress = $"모델 반입·검증 {copied / (1024 * 1024):N0}/{f.Size / (1024 * 1024):N0} MB";
                    }
                    if (copied != f.Size) throw new InvalidDataException("업데이트 파일이 중간에 잘렸습니다.");
                }
                var hash = Convert.ToHexString(hasher.GetHashAndReset());
                if (!hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"파일 검증 실패: {f.Path}");
            }
            await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(m, StudioSettings.Json), ct);
            var id = "bundle-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(stage, Path.Combine(BundlesRoot, id));
            Progress = "업데이트 검증 완료. 모델을 준비합니다.";
            return id;
        }
        finally
        {
            if (Directory.Exists(stage) && Path.GetFullPath(stage).StartsWith(Path.GetFullPath(BundlesRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(stage, true);
        }
    }
}
