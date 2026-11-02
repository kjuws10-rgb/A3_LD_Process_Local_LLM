using System.Text;
using System.Text.Json;

namespace A3.OfflineStudio.Core;

public sealed record ProposedEdit(string Path, string BaseSha256, string Content);
public sealed record ChangeProposal(string Id, string Title, DateTimeOffset CreatedUtc, IReadOnlyList<ProposedEdit> Edits, string Preview, string Status);

/// <summary>Proposals are inert until Apply is called explicitly by the user interface.</summary>
public sealed class ChangeStore
{
    private readonly RepositoryGuard _guard;
    private readonly string _state;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string StateDirectory => _state;

    public ChangeStore(RepositoryGuard guard, string stateDirectory)
    {
        _guard = guard;
        _guard.VerifyRepository();
        _state = Path.GetFullPath(stateDirectory);
        if (_state.Equals(guard.RootPath, StringComparison.OrdinalIgnoreCase) || _state.StartsWith(guard.RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("변경 이력/백업 저장소는 프로젝트 밖의 로컬 앱 데이터 폴더에 두어야 합니다.");
        RepositoryGuard.CheckNoReparsePoints(_state);
        Directory.CreateDirectory(Path.Combine(_state, "proposals"));
        Directory.CreateDirectory(Path.Combine(_state, "backups"));
        RecoverIncompleteTransactions();
    }

    public ChangeProposal Propose(string title, IReadOnlyList<ProposedEdit> edits)
    {
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            _guard.VerifyRepository();
            if (edits is null || edits.Count is < 1 or > 20) throw new InvalidOperationException("변경 제안에는 1~20개 파일이 필요합니다.");
            var normalized = NormalizeAndValidate(edits);
            var preview = new StringBuilder();
            foreach (var edit in normalized)
            {
                string full = _guard.ResolvePath(edit.Path, true);
                var before = File.Exists(full) ? TextFile.Read(full) : TextFile.New();
                string after = before.Encoding.GetString(before.Encode(edit.Content).AsSpan(before.Preamble.Length));
                preview.AppendLine(BuildPreview(edit.Path, before.Text, after));
            }
            var proposal = new ChangeProposal(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(title) ? "A3 코드 변경 제안" : title[..Math.Min(title.Length, 200)], DateTimeOffset.UtcNow, normalized, preview.ToString(), "pending");
            Save(proposal);
            return proposal;
        }
    }

    public IReadOnlyList<ChangeProposal> ListPending() => ListHistory(500).Where(p => p.Status == "pending").ToArray();

    public IReadOnlyList<ChangeProposal> ListHistory(int maxCount = 30)
    {
        lock (_gate)
        {
            var proposals = new List<ChangeProposal>();
            foreach (var path in Directory.EnumerateFiles(Path.Combine(_state, "proposals"), "*.json"))
            {
                RepositoryGuard.CheckNoReparsePoints(path);
                var proposal = ReadJson<ChangeProposal>(path);
                proposals.Add(proposal);
            }
            return proposals.OrderByDescending(p => p.CreatedUtc).Take(Math.Clamp(maxCount, 1, 500)).ToArray();
        }
    }

    public ChangeProposal Get(string id)
    {
        ValidateId(id);
        lock (_gate) return ReadJson<ChangeProposal>(ProposalPath(id));
    }

    public ChangeProposal Apply(string id)
    {
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            _guard.VerifyRepository();
            var proposal = Get(id);
            if (proposal.Status != "pending") throw new InvalidOperationException("아직 적용하지 않은 제안만 적용할 수 있습니다.");
            // Validate every source before creating a single destination or backup.
            var edits = NormalizeAndValidate(proposal.Edits);
            var transaction = new BackupManifest(id, _guard.RootPath, "prepared", []);
            var payloads = new List<byte[]>();
            var backupDir = BackupDirectory(id);
            if (Directory.Exists(backupDir)) throw new InvalidOperationException("이미 백업 이력이 있는 제안입니다. 새 제안을 만들어 주세요.");
            Directory.CreateDirectory(backupDir);
            for (int i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                string full = _guard.ResolvePath(edit.Path, true);
                bool exists = File.Exists(full);
                var original = exists ? TextFile.Read(full) : TextFile.New();
                if ((exists && !original.Sha256.Equals(edit.BaseSha256, StringComparison.OrdinalIgnoreCase)) || (!exists && edit.BaseSha256.Length != 0))
                    throw new InvalidOperationException("백업 준비 중 원본이 변경되었습니다: " + edit.Path + ". 새 제안을 만들어 주세요.");
                byte[] after = original.Encode(edit.Content);
                string backupName = $"{i:D3}.original";
                File.WriteAllBytes(Path.Combine(backupDir, backupName), original.Bytes);
                transaction.Files.Add(new(edit.Path, exists, exists ? original.Sha256 : "", TextFile.Hash(after), backupName));
                payloads.Add(after);
            }
            WriteJson(ManifestPath(id), transaction);
            try
            {
                for (int i = 0; i < transaction.Files.Count; i++)
                {
                    var entry = transaction.Files[i];
                    ValidateCurrent(entry.Path, entry.BeforeSha256, !entry.Existed);
                    AtomicWrite(_guard.ResolvePath(entry.Path, true), payloads[i]);
                }
                transaction = transaction with { Status = "applied" };
                WriteJson(ManifestPath(id), transaction);
                proposal = proposal with { Status = "applied" };
                Save(proposal);
                return proposal;
            }
            catch (Exception applyError)
            {
                try
                {
                    RollBackPrepared(transaction);
                    WriteJson(ManifestPath(id), transaction with { Status = "rolled_back" });
                    Save(proposal with { Status = "failed" });
                }
                catch (Exception rollbackError)
                {
                    throw new IOException($"변경 적용 실패 뒤 자동 복원이 중단되었습니다. 백업 폴더: {backupDir}. 다른 편집을 중단하고 백업을 확인해 주세요.", new AggregateException(applyError, rollbackError));
                }
                throw new IOException("변경 적용이 중단되어 원본으로 복원했습니다. 새로고침한 뒤 다시 제안해 주세요.", applyError);
            }
        }
    }

    public ChangeProposal Undo(string id)
    {
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            _guard.VerifyRepository();
            var proposal = Get(id);
            if (proposal.Status != "applied") throw new InvalidOperationException("적용된 제안만 되돌릴 수 있습니다.");
            var transaction = ReadJson<BackupManifest>(ManifestPath(id));
            ValidateManifest(transaction);
            if (transaction.Status != "applied") throw new InvalidOperationException("되돌릴 수 있는 백업 상태가 아닙니다.");
            var currentBytes = new List<byte[]>();
            var originals = new List<byte[]>();
            // Check all current versions first. Never overwrite edits made after Apply.
            foreach (var entry in transaction.Files)
            {
                ValidateCurrent(entry.Path, entry.AfterSha256, false);
                currentBytes.Add(File.ReadAllBytes(_guard.ResolvePath(entry.Path, true)));
                originals.Add(ReadOriginal(transaction.Id, entry));
            }
            WriteJson(ManifestPath(id), transaction with { Status = "undoing" });
            int written = 0;
            try
            {
                for (int i = 0; i < transaction.Files.Count; i++)
                {
                    var entry = transaction.Files[i];
                    ValidateCurrent(entry.Path, entry.AfterSha256, false);
                    string full = _guard.ResolvePath(entry.Path, true);
                    if (entry.Existed) AtomicWrite(full, originals[i]); else File.Delete(full);
                    written++;
                }
                WriteJson(ManifestPath(id), transaction with { Status = "undone" });
                proposal = proposal with { Status = "undone" };
                Save(proposal);
                return proposal;
            }
            catch (Exception undoError)
            {
                try
                {
                    for (int i = 0; i < written; i++)
                    {
                        var entry = transaction.Files[i];
                        ValidateCurrent(entry.Path, entry.BeforeSha256, !entry.Existed);
                        AtomicWrite(_guard.ResolvePath(entry.Path, true), currentBytes[i]);
                    }
                    WriteJson(ManifestPath(id), transaction);
                }
                catch (Exception rollbackError) { throw new IOException("되돌리기 도중 복원이 중단되었습니다. 백업 폴더를 확인해 주세요: " + BackupDirectory(id), new AggregateException(undoError, rollbackError)); }
                throw new IOException("되돌리기에 실패하여 적용 후 상태를 유지했습니다.", undoError);
            }
        }
    }

    public void Discard(string id)
    {
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            var proposal = Get(id);
            if (proposal.Status != "pending") throw new InvalidOperationException("대기 중인 제안만 버릴 수 있습니다.");
            Save(proposal with { Status = "discarded" });
        }
    }

    private IReadOnlyList<ProposedEdit> NormalizeAndValidate(IReadOnlyList<ProposedEdit> edits)
    {
        if (edits.Count is < 1 or > 20) throw new InvalidOperationException("파일 개수 제한을 초과했습니다.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProposedEdit>();
        foreach (var edit in edits)
        {
            string path = _guard.ToRelativePath(_guard.ResolvePath(edit.Path, true));
            if (!seen.Add(path)) throw new InvalidOperationException("한 제안에서 같은 파일을 여러 번 변경할 수 없습니다.");
            if (edit.Content is null || edit.Content.Length > TextFile.MaxBytes || edit.Content.Contains('\0')) throw new InvalidOperationException("변경 내용이 올바르지 않거나 너무 큽니다.");
            if (TextFile.SensitiveContent.IsMatch(edit.Content)) throw new UnauthorizedAccessException("인증정보를 포함하는 변경 제안은 저장할 수 없습니다.");
            string hash = edit.BaseSha256 ?? throw new InvalidOperationException("원본 SHA256 값이 필요합니다.");
            if (hash.Length != 0 && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) throw new InvalidOperationException("원본 SHA256 값이 잘못되었습니다.");
            ValidateCurrent(path, hash, hash.Length == 0);
            string full = _guard.ResolvePath(path, true);
            var source = File.Exists(full) ? TextFile.Read(full) : TextFile.New();
            if (TextFile.SensitiveContent.IsMatch(source.Text)) throw new UnauthorizedAccessException("인증정보가 포함된 파일은 변경할 수 없습니다.");
            _ = source.Encode(edit.Content);
            result.Add(new(path, hash.ToLowerInvariant(), edit.Content));
        }
        return result;
    }

    private void ValidateCurrent(string path, string expectedHash, bool mustNotExist)
    {
        string full = _guard.ResolvePath(path, true);
        bool exists = File.Exists(full);
        if (mustNotExist)
        {
            if (exists || Directory.Exists(full)) throw new InvalidOperationException($"새 파일 경로에 이미 파일이 있습니다: {path}");
        }
        else if (!exists || !TextFile.Read(full).Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"원본이 변경되어 작업을 중단했습니다: {path}. 새로고침 후 다시 제안해 주세요.");
    }

    private void RecoverIncompleteTransactions()
    {
        lock (_gate)
        {
            using var fileLock = AcquireLock();
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(_state, "backups")))
            {
                RepositoryGuard.CheckNoReparsePoints(dir);
                string path = Path.Combine(dir, "manifest.json");
                if (!File.Exists(path)) continue; // A preparation interrupted before any repository write.
                var manifest = ReadJson<BackupManifest>(path);
                ValidateManifest(manifest);
                if (manifest.Status == "prepared")
                {
                    RollBackPrepared(manifest);
                    WriteJson(path, manifest with { Status = "rolled_back" });
                    var proposal = Get(manifest.Id);
                    Save(proposal with { Status = "failed" });
                }
                else if (manifest.Status == "undoing")
                {
                    // Finish a previously approved undo only if every file is still an expected version.
                    ValidateRollbackVersions(manifest);
                    foreach (var entry in manifest.Files)
                    {
                        string full = _guard.ResolvePath(entry.Path, true);
                        if (entry.Existed) AtomicWrite(full, ReadOriginal(manifest.Id, entry)); else if (File.Exists(full)) File.Delete(full);
                    }
                    WriteJson(path, manifest with { Status = "undone" });
                    Save(Get(manifest.Id) with { Status = "undone" });
                }
                else if (manifest.Status == "applied" && Get(manifest.Id).Status == "pending")
                {
                    // Crash after all atomic writes and durable manifest but before proposal status save.
                    foreach (var entry in manifest.Files) ValidateCurrent(entry.Path, entry.AfterSha256, false);
                    Save(Get(manifest.Id) with { Status = "applied" });
                }
            }
        }
    }

    private void ValidateManifest(BackupManifest manifest)
    {
        ValidateId(manifest.Id);
        if (!manifest.RepositoryRoot.Equals(_guard.RootPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("백업 저장소가 다른 프로젝트에 속합니다.");
        if (manifest.Files.Count is < 1 or > 20) throw new InvalidDataException("백업 목록이 잘못되었습니다.");
        foreach (var file in manifest.Files)
        {
            _ = _guard.ResolvePath(file.Path, true);
            if (!RegexBackupName(file.BackupName)) throw new InvalidDataException("백업 파일 경로가 잘못되었습니다.");
        }
    }

    private static bool RegexBackupName(string name) => name.Length == 12 && name.EndsWith(".original", StringComparison.Ordinal) && name[..3].All(char.IsAsciiDigit);

    private void ValidateRollbackVersions(BackupManifest transaction)
    {
        ValidateManifest(transaction);
        foreach (var entry in transaction.Files)
        {
            string full = _guard.ResolvePath(entry.Path, true);
            string hash = File.Exists(full) ? TextFile.Read(full).Sha256 : "";
            if (hash != entry.BeforeSha256 && hash != entry.AfterSha256)
                throw new InvalidOperationException($"중단된 변경 뒤 파일이 다시 편집되었습니다. 자동 복원을 중지합니다: {entry.Path}. 백업: {BackupDirectory(transaction.Id)}");
            _ = ReadOriginal(transaction.Id, entry);
        }
    }

    private void RollBackPrepared(BackupManifest transaction)
    {
        ValidateManifest(transaction);
        var conflicts = new List<Exception>();
        foreach (var entry in transaction.Files.AsEnumerable().Reverse())
        {
            try
            {
                string full = _guard.ResolvePath(entry.Path, true);
                string hash = File.Exists(full) ? TextFile.Read(full).Sha256 : "";
                if (hash == entry.BeforeSha256) continue;
                if (hash != entry.AfterSha256) throw new InvalidOperationException("다른 편집이 감지되어 해당 파일은 보존합니다: " + entry.Path);
                if (entry.Existed) AtomicWrite(full, ReadOriginal(transaction.Id, entry));
                else if (File.Exists(full)) File.Delete(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { conflicts.Add(ex); }
        }
        if (conflicts.Count > 0) throw new AggregateException("일부 파일은 다른 편집 또는 접근 제한으로 자동 복원하지 못했습니다.", conflicts);
    }

    private byte[] ReadOriginal(string id, BackupFile entry)
    {
        if (!RegexBackupName(entry.BackupName)) throw new InvalidDataException("백업 이름이 잘못되었습니다.");
        string backup = Path.Combine(BackupDirectory(id), entry.BackupName);
        RepositoryGuard.CheckNoReparsePoints(backup);
        byte[] bytes = File.ReadAllBytes(backup);
        if (bytes.Length > TextFile.MaxBytes || (entry.Existed && TextFile.Hash(bytes) != entry.BeforeSha256)) throw new InvalidDataException("백업 파일 무결성 검증에 실패했습니다.");
        return bytes;
    }

    private void AtomicWrite(string path, byte[] content)
    {
        RepositoryGuard.CheckNoReparsePoints(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RepositoryGuard.CheckNoReparsePoints(path);
        string temporary = path + ".a3tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(content); stream.Flush(true); }
            RepositoryGuard.CheckNoReparsePoints(path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private FileStream AcquireLock()
    {
        RepositoryGuard.CheckNoReparsePoints(_state);
        try { return new FileStream(Path.Combine(_state, "change.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("다른 창에서 변경 이력을 처리 중입니다. 잠시 후 다시 시도해 주세요.", ex); }
    }
    private static void ValidateId(string id) { if (id is null || id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new ArgumentException("제안 ID가 잘못되었습니다."); }
    private string ProposalPath(string id) { ValidateId(id); return Path.Combine(_state, "proposals", id + ".json"); }
    private string BackupDirectory(string id) { ValidateId(id); return Path.Combine(_state, "backups", id); }
    private string ManifestPath(string id) => Path.Combine(BackupDirectory(id), "manifest.json");
    private void Save(ChangeProposal proposal) => WriteJson(ProposalPath(proposal.Id), proposal);
    private static T ReadJson<T>(string path)
    {
        RepositoryGuard.CheckNoReparsePoints(path);
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("이력 파일이 너무 큽니다.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("이력 파일이 손상되었습니다.");
    }
    private static void WriteJson<T>(string path, T value)
    {
        RepositoryGuard.CheckNoReparsePoints(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string BuildPreview(string path, string before, string after)
    {
        var a = ProjectIndex.Lines(before);
        var b = ProjectIndex.Lines(after);
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        if (prefix == a.Length && prefix == b.Length) return $"--- {path}\n+++ {path}\n(변경 없음)\n";
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        int contextStart = Math.Max(0, prefix - 3);
        int aEnd = Math.Min(a.Length, a.Length - suffix + 3);
        int bEnd = Math.Min(b.Length, b.Length - suffix + 3);
        var output = new StringBuilder($"--- {path}\n+++ {path}\n@@ -{contextStart + 1},{aEnd - contextStart} +{contextStart + 1},{bEnd - contextStart} @@\n");
        for (int i = contextStart; i < prefix; i++) output.Append(' ').AppendLine(a[i]);
        for (int i = prefix; i < a.Length - suffix; i++) { output.Append('-').AppendLine(a[i]); if (output.Length > 120_000) break; }
        for (int i = prefix; i < b.Length - suffix; i++) { output.Append('+').AppendLine(b[i]); if (output.Length > 240_000) break; }
        for (int i = a.Length - suffix; i < aEnd; i++) output.Append(' ').AppendLine(a[i]);
        if (output.Length > 240_000) output.AppendLine("[긴 미리보기 일부 생략; 전체 변경 내용은 제안 파일에 보존됨]");
        return output.ToString();
    }

    private sealed record BackupFile(string Path, bool Existed, string BeforeSha256, string AfterSha256, string BackupName);
    private sealed record BackupManifest(string Id, string RepositoryRoot, string Status, List<BackupFile> Files);
}
