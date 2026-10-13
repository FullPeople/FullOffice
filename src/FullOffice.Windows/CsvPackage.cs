using System.Text;

namespace FlowOfficeLab;

static class CsvPackage
{
    public static void Validate(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        Span<byte> header = stackalloc byte[4]; int length = input.Read(header);
        Encoding encoding = Encoding.Latin1; int skip = 0;
        // Delimiters and quoting have the same byte values in UTF-8 and common
        // ANSI encodings. BOM-based Unicode files are decoded strictly.
        if (length >= 4 && header.SequenceEqual(new byte[] { 255, 254, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); skip = 4; }
        else if (length >= 4 && header.SequenceEqual(new byte[] { 0, 0, 254, 255 })) { encoding = new UTF32Encoding(true, false, true); skip = 4; }
        else if (length >= 2 && header[0] == 255 && header[1] == 254) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (length >= 2 && header[0] == 254 && header[1] == 255) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        else if (length >= 3 && header[0] == 239 && header[1] == 187 && header[2] == 191) skip = 3;
        input.Position = skip;
        using var text = new StreamReader(input, encoding, false, 65536);
        bool quoted = false;
        try {
            while (text.Read() is int value && value >= 0) {
                if (value < 32 && value is not (9 or 10 or 13)) throw new IOException("CSV 文件中存在无效的二进制内容。");
                if (value != '"') continue;
                if (quoted && text.Peek() == '"') { text.Read(); continue; }
                quoted = !quoted;
            }
        } catch (DecoderFallbackException error) { throw new IOException("CSV 文件的 Unicode 编码不完整。", error); }
        if (quoted) throw new IOException("CSV 的引号内容不完整，无法替换原文件。");
    }
}
