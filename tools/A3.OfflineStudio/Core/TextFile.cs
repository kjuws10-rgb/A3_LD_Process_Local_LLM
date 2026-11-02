using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace A3.OfflineStudio.Core;

internal sealed record TextFile(string Text, Encoding Encoding, byte[] Preamble, string NewLine, byte[] Bytes)
{
    internal const int MaxBytes = 1_048_576;
    internal string Sha256 => Hash(Bytes);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static readonly Regex SensitiveContent = new(@"-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----|(?:api[_-]?key|access[_-]?token|client[_-]?secret|password|passwd)\s*[""']?\s*[:=]\s*[""'][^""'\r\n]{6,}[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static TextFile Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("파일을 찾을 수 없습니다.", path);
        if (info.Length > MaxBytes) throw new InvalidOperationException("1MB를 초과하는 파일은 모델에 제공하거나 변경할 수 없습니다.");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("파일 크기 제한을 초과했습니다.");
        Encoding encoding;
        byte[] preamble;
        int skip;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(false, true); preamble = [0xEF, 0xBB, 0xBF]; skip = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); preamble = [0xFF, 0xFE]; skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); preamble = [0xFE, 0xFF]; skip = 2; }
        else { encoding = new UTF8Encoding(false, true); preamble = []; skip = 0; }
        string text;
        try { text = encoding.GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) when (skip == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = encoding.GetString(bytes);
        }
        if (text.Contains('\0') || text.Any(c => c < 32 && c is not '\r' and not '\n' and not '\t' and not '\f'))
            throw new InvalidDataException("바이너리 또는 지원하지 않는 텍스트 파일입니다.");
        return new(text, encoding, preamble, text.Contains("\r\n") ? "\r\n" : "\n", bytes);
    }

    internal byte[] Encode(string content)
    {
        if (content.Contains('\0')) throw new InvalidDataException("변경 내용에 NULL 문자를 사용할 수 없습니다.");
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", NewLine);
        byte[] body;
        try { body = Encoding.GetBytes(normalized); }
        catch (EncoderFallbackException) { throw new InvalidOperationException("기존 파일 인코딩으로 표현할 수 없는 문자가 포함되었습니다. 변경 내용을 확인해 주세요."); }
        if (body.Length + Preamble.Length > MaxBytes) throw new InvalidOperationException("변경 결과가 1MB 제한을 초과합니다.");
        return [.. Preamble, .. body];
    }

    internal static TextFile New() => new("", new UTF8Encoding(false, true), [], "\n", []);
}
