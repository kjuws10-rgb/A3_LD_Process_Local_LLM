using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace A3.OfflineStudio.Core;

/// <summary>All model-visible paths pass this boundary; Git metadata is read only by this class.</summary>
public sealed class RepositoryGuard
{
    private static readonly HashSet<string> DeniedSegments = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", "bin", "obj", "runtime", "data", "log", "logs", "secrets", "node_modules", "packages", "models", ".offline-studio" };
    private static readonly Regex SecretName = new(@"(^\.env($|\.))|(^id_(rsa|dsa|ed25519)$)|(secret|credential|password|api[-_]?key)|\.(pfx|p12|pem|key|kdbx)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReservedName = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    public string RootPath { get; }

    public RepositoryGuard(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(RootPath)) throw new DirectoryNotFoundException("프로젝트 폴더를 찾을 수 없습니다.");
        CheckNoReparsePoints(RootPath);
    }

    public void VerifyRepository()
    {
        CheckNoReparsePoints(RootPath);
        var git = GetGitDirectory();
        var common = git;
        var commonFile = Path.Combine(git, "commondir");
        if (File.Exists(commonFile))
        {
            CheckNoReparsePoints(commonFile);
            common = Path.GetFullPath(Path.Combine(git, File.ReadAllText(commonFile).Trim()));
            CheckNoReparsePoints(common);
        }
        var config = Path.Combine(common, "config");
        CheckNoReparsePoints(config);
        if (!File.Exists(config) || new FileInfo(config).Length > 1_048_576)
            throw new InvalidOperationException("Git origin 설정을 확인할 수 없습니다.");
        string? remote = null;
        bool originSection = false;
        foreach (var raw in File.ReadLines(config))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { originSection = Regex.IsMatch(line, "^\\[remote\\s+\"origin\"\\]$", RegexOptions.IgnoreCase); continue; }
            if (originSection && Regex.IsMatch(line, @"^url\s*=", RegexOptions.IgnoreCase))
            {
                if (remote is not null) throw new InvalidOperationException("Git origin URL이 여러 개여서 전용 저장소를 확인할 수 없습니다.");
                remote = line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
            }
        }
        if (!IsDedicatedRemote(remote))
            throw new InvalidOperationException("이 앱은 kjuws10-rgb/A3_LD_Process_SW_IPS 저장소 전용입니다. origin URL을 확인해 주세요.");
        if (!Directory.EnumerateFiles(RootPath, "*.sln", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidOperationException("A3 솔루션(.sln)이 있는 저장소 루트를 선택해 주세요.");
    }

    public static bool IsDedicatedRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        return Regex.IsMatch(remote.Trim(), @"^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)kjuws10-rgb/A3_LD_Process_SW_IPS(\.git)?/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string ResolvePath(string relative, bool forWrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        if (relative.Length > 1000) throw new UnauthorizedAccessException("파일 경로가 너무 깁니다.");
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new UnauthorizedAccessException("프로젝트 안의 상대 경로만 사용할 수 있습니다.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "" or "." or ".." || ReservedName.IsMatch(p) || p.TrimEnd(' ', '.') != p || p.Any(c => c < 32) || p.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0))
            throw new UnauthorizedAccessException("상위 경로 이동이나 잘못된 경로는 허용되지 않습니다.");
        if (parts.Any(DeniedSegments.Contains) || parts[^1].Equals(".approval.json", StringComparison.OrdinalIgnoreCase) || SecretName.IsMatch(parts[^1]))
            throw new UnauthorizedAccessException("Git 메타데이터·실행파일·로그·인증정보 영역은 접근할 수 없습니다.");
        var full = Path.GetFullPath(Path.Combine(RootPath, Path.Combine(parts)));
        if (!full.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("프로젝트 밖의 파일은 접근할 수 없습니다.");
        CheckNoReparsePoints(full);
        if (Directory.Exists(full)) throw new UnauthorizedAccessException("파일 경로만 사용할 수 있습니다.");
        if (forWrite && !ProjectIndex.IsSupportedPath(relative))
            throw new UnauthorizedAccessException("허용된 텍스트 소스 파일만 변경할 수 있습니다.");
        return full;
    }

    public string ToRelativePath(string full)
    {
        var relative = Path.GetRelativePath(RootPath, Path.GetFullPath(full)).Replace('\\', '/');
        _ = ResolvePath(relative);
        return relative;
    }

    internal string GetGitDirectory()
    {
        var marker = Path.Combine(RootPath, ".git");
        CheckNoReparsePoints(marker);
        if (Directory.Exists(marker)) return marker;
        if (!File.Exists(marker) || new FileInfo(marker).Length > 4096)
            throw new InvalidOperationException("Git 저장소 또는 worktree 메타데이터가 필요합니다.");
        var text = File.ReadAllText(marker).Trim();
        if (!text.StartsWith("gitdir: ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("올바른 Git worktree가 아닙니다.");
        var target = Path.GetFullPath(Path.Combine(RootPath, text[8..].Trim()));
        CheckNoReparsePoints(target);
        if (!Directory.Exists(target)) throw new InvalidOperationException("Git worktree 원본 메타데이터를 찾을 수 없습니다. 오프라인 복사본에는 전체 .git 폴더가 필요합니다.");
        return target;
    }

    // Reading the index directly keeps retrieval offline and never launches Git or a shell.
    internal IReadOnlyList<string> GetTrackedPaths()
    {
        VerifyRepository();
        var index = Path.Combine(GetGitDirectory(), "index");
        CheckNoReparsePoints(index);
        if (!File.Exists(index)) throw new InvalidOperationException("Git index가 없습니다. 정상 checkout한 저장소를 선택해 주세요.");
        if (new FileInfo(index).Length > 64 * 1024 * 1024) throw new InvalidOperationException("Git index 크기가 허용 범위를 초과했습니다.");
        var bytes = File.ReadAllBytes(index);
        if (bytes.Length < 32 || Encoding.ASCII.GetString(bytes, 0, 4) != "DIRC") throw new InvalidDataException("Git index 형식이 잘못되었습니다.");
        int version = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)));
        int count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)));
        if (version is < 2 or > 4 || count > 500_000) throw new InvalidDataException("지원되지 않는 Git index입니다. index version 2~4를 사용해 주세요.");
        var paths = new List<string>();
        int offset = 12;
        byte[] previous = [];
        for (int i = 0; i < count; i++)
        {
            int start = offset;
            if (offset + 62 > bytes.Length - 20) throw new InvalidDataException("Git index 항목이 잘렸습니다.");
            uint mode = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 24, 4));
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 60, 2));
            offset += 62;
            ushort extended = 0;
            if ((flags & 0x4000) != 0)
            {
                if (offset + 2 > bytes.Length - 20) throw new InvalidDataException("Git index 확장 항목이 잘렸습니다.");
                extended = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2)); offset += 2;
            }
            int remove = 0;
            if (version == 4)
            {
                int n = 0;
                byte b;
                do { if (offset >= bytes.Length - 20 || n > 1_000_000) throw new InvalidDataException("Git index 경로 압축 오류입니다."); b = bytes[offset++]; n = (n << 7) + (b & 0x7f); if ((b & 0x80) != 0) n++; } while ((b & 0x80) != 0);
                remove = n;
            }
            int end = Array.IndexOf(bytes, (byte)0, offset);
            if (end < offset || end >= bytes.Length - 20) throw new InvalidDataException("Git index 경로가 잘렸습니다.");
            if (remove > previous.Length) throw new InvalidDataException("Git index 경로 압축이 잘못되었습니다.");
            byte[] pathBytes = version == 4 ? [.. previous.AsSpan(0, previous.Length - remove).ToArray(), .. bytes.AsSpan(offset, end - offset).ToArray()] : bytes.AsSpan(offset, end - offset).ToArray();
            string path = new UTF8Encoding(false, true).GetString(pathBytes);
            previous = pathBytes;
            offset = end + 1;
            if (version != 4) offset = start + ((offset - start + 7) / 8 * 8);
            if ((flags & 0x3000) != 0 || (extended & 0x4000) != 0 || (mode & 0xF000) != 0x8000) continue; // conflict, skip-worktree, symlink, submodule
            paths.Add(path);
        }
        return paths;
    }

    internal static void CheckNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        string? current = full;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                // File.Exists is false for a dangling link; GetAttributes still identifies the reparse point.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("심볼릭 링크·junction 경로는 접근할 수 없습니다.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }
}
