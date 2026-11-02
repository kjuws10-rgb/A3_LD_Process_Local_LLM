using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace A3.OfflineStudio;

/// <summary>Offline Office text extraction. Formulas use stored cached values; drawings/images are not interpreted.</summary>
public static class DocumentExtractor
{
    public static string Extract(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("관련 자료는 파일당 32MB까지 추가할 수 있습니다.");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 5000 || zip.Entries.Sum(e => e.Length) > 128 * 1024 * 1024) throw new InvalidDataException("문서 내부 데이터가 너무 큽니다.");
        var result = new StringBuilder("원본 자료: " + Path.GetFileName(path) + "\n주의: 이미지·도형·매크로는 분석하지 않습니다. 수식은 저장된 결과를 사용합니다.\n\n");
        XDocument? Read(string name)
        {
            var entry = zip.GetEntry(name); if (entry is null) return null;
            if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("문서의 한 파트가 너무 큽니다.");
            using var stream = entry.Open(); using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 }); return XDocument.Load(reader);
        }
        if (Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            var doc = Read("word/document.xml") ?? throw new InvalidDataException("지원하는 Word 문서가 아닙니다.");
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            foreach (var p in doc.Descendants(w + "p")) result.AppendLine(string.Concat(p.Descendants().Where(e => e.Name == w + "t" || e.Name == w + "tab" || e.Name == w + "br").Select(e => e.Name == w + "t" ? e.Value : e.Name == w + "tab" ? "\t" : "\n")));
        }
        else if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var shared = Read("xl/sharedStrings.xml")?.Descendants(s + "si").Select(si => string.Concat(si.Descendants(s + "t").Select(t => t.Value))).ToArray() ?? [];
            var relations = Read("xl/_rels/workbook.xml.rels")?.Root?.Elements().Where(e => (string?)e.Attribute("TargetMode") != "External").ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!) ?? [];
            var workbook = Read("xl/workbook.xml") ?? throw new InvalidDataException("지원하는 Excel 문서가 아닙니다.");
            foreach (var sheet in workbook.Descendants(s + "sheet"))
            {
                result.AppendLine("\n[시트: " + (string?)sheet.Attribute("name") + "]");
                if (!relations.TryGetValue((string?)sheet.Attribute(rel + "id") ?? "", out var target)) continue;
                if (target.Contains("..") || target.Contains(':') || target.Contains('\\')) throw new InvalidDataException("Excel 시트 경로가 잘못되었습니다.");
                var name = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                var data = Read(name); if (data is null) continue;
                foreach (var row in data.Descendants(s + "row"))
                {
                    foreach (var cell in row.Elements(s + "c"))
                    {
                        var value = (string?)cell.Element(s + "v") ?? string.Concat(cell.Descendants(s + "t").Select(t => t.Value));
                        if ((string?)cell.Attribute("t") == "s" && int.TryParse(value, out var n) && n >= 0 && n < shared.Length) value = shared[n];
                        if (!string.IsNullOrWhiteSpace(value)) result.Append((string?)cell.Attribute("r")).Append('=').Append(value.Replace('\n', ' ')).Append('\t');
                    }
                    result.AppendLine();
                    if (result.Length > 1_500_000) throw new InvalidDataException("문서 텍스트가 너무 큽니다. 필요한 시트만 CSV로 추가하세요.");
                }
            }
        }
        else throw new InvalidDataException("Word(.docx)와 Excel(.xlsx) 문서를 지원합니다.");
        if (Encoding.UTF8.GetByteCount(result.ToString()) > 1_500_000) throw new InvalidDataException("추출한 문서 텍스트가 너무 큽니다.");
        return result.ToString();
    }
}
