using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace A3.OfflineStudio.Core;

public sealed record SearchHit(string Path, int StartLine, int EndLine, string Sha256, string Text, double Score)
{
    public string Citation => $"{Path}:{StartLine}-{EndLine} (sha256:{Sha256[..Math.Min(12, Sha256.Length)]})";
}

/// <summary>A local lexical index of Git-tracked text and explicitly imported documents.</summary>
public sealed class ProjectIndex
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".xaml", ".md", ".txt", ".json", ".csv", ".xml", ".config", ".sln", ".csproj", ".props", ".targets", ".resx", ".yaml", ".yml", ".ini", ".editorconfig", ".gitignore", ".gitattributes", ".html", ".css", ".js", ".ts", ".sql" };
    private static readonly Regex Words = new(@"[A-Za-z_][A-Za-z_0-9]*|[가-힣]+|\p{N}+|[\p{L}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Korean = new(@"[가-힣]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly RepositoryGuard _guard;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private List<Chunk> _chunks = [];
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    public int FileCount { get { lock (_gate) return _paths.Count; } }
    public int ChunkCount { get { lock (_gate) return _chunks.Count; } }
    public DateTimeOffset? LastRefreshedUtc { get; private set; }
    public RepositoryGuard Repository => _guard;
    public event Action<int, int, string>? Progress;

    public ProjectIndex(RepositoryGuard guard) => _guard = guard;
    public static bool IsSupportedPath(string path) => Extensions.Contains(Path.GetExtension(path)) || Extensions.Contains(Path.GetFileName(path));

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var candidates = _guard.GetTrackedPaths().Where(p => !p.StartsWith("approved-docs/", StringComparison.OrdinalIgnoreCase)).Concat(LoadApprovedDocuments()).Concat(LoadProjectFiles()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (candidates.Length > 50_000) throw new InvalidOperationException("인덱싱할 파일이 너무 많습니다(최대 50,000개).");
                var chunks = new List<Chunk>();
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<Chunk>> previousChunks;
                lock (_gate) previousChunks = _chunks.GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                long total = 0;
                int processed = 0;
                foreach (var path in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Progress?.Invoke(++processed, candidates.Length, path);
                    if (!IsSupportedPath(path)) continue;
                    try
                    {
                        var full = _guard.ResolvePath(path);
                        if (!File.Exists(full)) continue;
                        var file = TextFile.Read(full);
                        if (TextFile.SensitiveContent.IsMatch(file.Text)) continue;
                        total += file.Bytes.Length;
                        if (total > 64 * 1024 * 1024) throw new InvalidOperationException("프로젝트 텍스트 크기가 64MB 제한을 초과합니다.");
                        paths.Add(path.Replace('\\', '/'));
                        string sourceHash = file.Sha256;
                        if (previousChunks.TryGetValue(path, out var cached) && cached.Count > 0 && cached[0].Sha256 == sourceHash)
                        {
                            chunks.AddRange(cached);
                            continue;
                        }
                        var lines = Lines(file.Text);
                        for (int start = 0; start < lines.Length;)
                        {
                            int end = start;
                            int size = 0;
                            while (end < lines.Length && end - start < 50 && (size < 2600 || end == start)) { size += lines[end].Length + 1; end++; }
                            var text = string.Join('\n', lines[start..end]);
                            if (text.Length > 6000) text = text[..6000] + "\n[긴 줄 생략]";
                            var terms = Tokens(path + "\n" + text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                            chunks.Add(new(path.Replace('\\', '/'), start + 1, end, sourceHash, text, terms, Math.Max(1, terms.Values.Sum())));
                            if (end >= lines.Length) break;
                            start = Math.Max(start + 1, end - 5);
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DecoderFallbackException) { /* Excluded file: binary, missing, locked, link, private or unsupported encoding. */ }
                    catch (InvalidOperationException ex) when (!ex.Message.Contains("64MB", StringComparison.Ordinal)) { }
                }
                lock (_gate) { _chunks = chunks; _paths = paths; LastRefreshedUtc = DateTimeOffset.UtcNow; }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _refreshGate.Release(); }
    }

    public IReadOnlyList<SearchHit> Search(string query, int maxResults = 6, int maxChars = 16_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        maxResults = Math.Clamp(maxResults, 1, 12);
        maxChars = Math.Clamp(maxChars, 256, 24_000);
        var tokens = Tokens(query[..Math.Min(query.Length, 2000)]).Distinct().Take(80).ToArray();
        List<Chunk> snapshot;
        lock (_gate) snapshot = _chunks;
        if (tokens.Length == 0 || snapshot.Count == 0) return [];
        double average = snapshot.Average(c => c.Length);
        var frequency = tokens.ToDictionary(t => t, t => snapshot.Count(c => c.Terms.ContainsKey(t)));
        var ranked = snapshot.Select(c =>
        {
            double score = 0;
            foreach (var token in tokens)
            {
                if (!c.Terms.TryGetValue(token, out int count)) continue;
                double idf = Math.Log(1 + (snapshot.Count - frequency[token] + .5) / (frequency[token] + .5));
                score += idf * count * 2.2 / (count + 1.2 * (.25 + .75 * c.Length / average));
                if (c.Path.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 1.2;
            }
            return (chunk: c, score);
        }).Where(x => x.score > 0).OrderByDescending(x => x.score);
        var result = new List<SearchHit>();
        int used = 0;
        foreach (var (chunk, score) in ranked)
        {
            if (result.Count >= maxResults || maxChars - used < 100) break;
            if (result.Any(h => h.Path.Equals(chunk.Path, StringComparison.OrdinalIgnoreCase) && h.StartLine <= chunk.End && h.EndLine >= chunk.Start)) continue;
            try
            {
                // Always reread: returned hash and source lines describe the current file, not a stale index.
                var hit = ReadInternal(chunk.Path, chunk.Start, chunk.End - chunk.Start + 1, Math.Min(6000, maxChars - used), score, requireIndexed: true);
                result.Add(hit); used += hit.Text.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException) { }
        }
        return result;
    }

    public SearchHit Read(string path, int startLine = 1, int maxLines = 160) => ReadInternal(path, startLine, maxLines, 12_000, 0, true);

    internal SearchHit ReadBounded(string path, int startLine, int maxLines, int maxChars) => ReadInternal(path, startLine, maxLines, maxChars, 0, true);

    private SearchHit ReadInternal(string path, int startLine, int maxLines, int maxChars, double score, bool requireIndexed)
    {
        string relative = _guard.ToRelativePath(_guard.ResolvePath(path));
        lock (_gate) if (requireIndexed && !_paths.Contains(relative)) throw new UnauthorizedAccessException("Git 추적 파일 또는 사용자가 가져온 자료만 읽을 수 있습니다. 먼저 프로젝트를 새로고침해 주세요.");
        var file = TextFile.Read(_guard.ResolvePath(relative));
        if (TextFile.SensitiveContent.IsMatch(file.Text)) throw new UnauthorizedAccessException("인증정보가 포함된 것으로 보이는 파일은 모델에 제공하지 않습니다.");
        var lines = Lines(file.Text);
        startLine = Math.Clamp(startLine, 1, Math.Max(1, lines.Length));
        maxLines = Math.Clamp(maxLines, 1, 250);
        int end = Math.Min(lines.Length, startLine - 1 + maxLines);
        var text = new StringBuilder();
        int actualEnd = startLine;
        for (int i = startLine - 1; i < end; i++)
        {
            int available = maxChars - text.Length;
            if (available < 1) break;
            var line = lines[i];
            text.Append(line[..Math.Min(line.Length, available)]);
            actualEnd = i + 1;
            if (line.Length > available) { text.Append(" [줄 생략]"); break; }
            if (i + 1 < end) text.Append('\n');
        }
        return new(relative, startLine, actualEnd, file.Sha256, text.ToString(), score);
    }

    public string ImportDocument(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        RepositoryGuard.CheckNoReparsePoints(sourcePath);
        if (!IsSupportedPath(sourcePath)) throw new InvalidOperationException("텍스트 자료(.md, .txt, .csv, .json 등)만 가져올 수 있습니다. PDF/Office는 텍스트로 내보낸 뒤 가져와 주세요.");
        var source = TextFile.Read(sourcePath);
        if (TextFile.SensitiveContent.IsMatch(source.Text)) throw new UnauthorizedAccessException("인증정보가 포함된 자료는 가져올 수 없습니다.");
        var filename = Path.GetFileNameWithoutExtension(sourcePath);
        filename = string.Concat(filename.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        if (filename.Length > 80) filename = filename[..80];
        if (filename.Length == 0) filename = "document";
        string relative = $"approved-docs/{filename}-{source.Sha256[..12]}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
        string target = _guard.ResolvePath(relative, forWrite: true);
        lock (_gate)
        {
            var approved = LoadApprovedDocuments().ToHashSet(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            RepositoryGuard.CheckNoReparsePoints(target);
            if (File.Exists(target) && TextFile.Hash(File.ReadAllBytes(target)) != source.Sha256) throw new IOException("같은 이름의 자료가 변경되어 가져오기를 중단했습니다.");
            if (!File.Exists(target)) File.WriteAllBytes(target, source.Bytes);
            // A reimport of the same named document supersedes its old active version. Preserve old bytes.
            string stem = $"approved-docs/{filename}-";
            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            approved.RemoveWhere(existing =>
            {
                if (!existing.StartsWith(stem, StringComparison.OrdinalIgnoreCase) || !existing.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
                string suffix = existing[stem.Length..^extension.Length];
                return suffix.Length == 12 && suffix.All(Uri.IsHexDigit);
            });
            approved.Add(relative);
            var catalog = CatalogPath();
            var temp = catalog + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, JsonSerializer.Serialize(approved.OrderBy(p => p)), new UTF8Encoding(false)); File.Move(temp, catalog, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return relative;
    }

    /// <summary>Called by the UI after a user-approved Apply so new source files remain searchable before Git add.</summary>
    public void RegisterProjectFile(string path)
    {
        string relative = _guard.ToRelativePath(_guard.ResolvePath(path, true));
        var source = TextFile.Read(_guard.ResolvePath(relative));
        if (TextFile.SensitiveContent.IsMatch(source.Text)) throw new UnauthorizedAccessException("인증정보가 있는 파일은 등록할 수 없습니다.");
        lock (_gate)
        {
            var paths = LoadProjectFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);
            paths.Add(relative);
            string catalog = ProjectFilesPath();
            Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
            RepositoryGuard.CheckNoReparsePoints(catalog);
            string temporary = catalog + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(paths.OrderBy(p => p)), new UTF8Encoding(false)); File.Move(temporary, catalog, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private string ProjectFilesPath()
    {
        string path = Path.Combine(_guard.RootPath, ".offline-studio", "project-files.json");
        RepositoryGuard.CheckNoReparsePoints(path);
        return path;
    }

    private IReadOnlyList<string> LoadProjectFiles()
    {
        string catalog = ProjectFilesPath();
        if (!File.Exists(catalog)) return [];
        if (new FileInfo(catalog).Length > 256_000) throw new InvalidDataException("추가 소스 등록 목록이 너무 큽니다.");
        var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(catalog)) ?? [];
        foreach (string path in list) _ = _guard.ResolvePath(path, true);
        return list;
    }

    private string CatalogPath()
    {
        string catalog = Path.Combine(_guard.RootPath, "approved-docs", ".approval.json");
        RepositoryGuard.CheckNoReparsePoints(catalog);
        return catalog;
    }

    private IReadOnlyList<string> LoadApprovedDocuments()
    {
        string catalog = CatalogPath();
        if (!File.Exists(catalog)) return [];
        if (new FileInfo(catalog).Length > 256_000) throw new InvalidDataException("자료 승인 목록이 너무 큽니다.");
        var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(catalog)) ?? [];
        if (list.Any(p => !p.StartsWith("approved-docs/", StringComparison.Ordinal) || p.Contains("..", StringComparison.Ordinal))) throw new InvalidDataException("자료 승인 목록이 잘못되었습니다.");
        return list;
    }

    internal static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    private static IEnumerable<string> Tokens(string text)
    {
        foreach (Match match in Words.Matches(text.ToLowerInvariant()))
        {
            string word = match.Value;
            if (word.Length > 1) yield return word;
            foreach (Match korean in Korean.Matches(word))
                for (int i = 0; i < korean.Length - 1; i++) yield return korean.Value.Substring(i, 2);
            foreach (string part in word.Split('_', StringSplitOptions.RemoveEmptyEntries)) if (part.Length > 1 && part != word) yield return part;
        }
    }

    private sealed record Chunk(string Path, int Start, int End, string Sha256, string Text, Dictionary<string, int> Terms, int Length);
}
