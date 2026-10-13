using System.Globalization;
using System.Text;

namespace FlowOfficeLab;

static class RichTextPackage
{
    public static void Validate(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        Span<byte> prefix = stackalloc byte[6]; input.ReadExactly(prefix);
        if (!prefix.SequenceEqual("{\\rtf1"u8)) throw new IOException("文档内容与 RTF 格式不匹配。");
        long depth = 1; bool first = true;
        while (input.ReadByte() is int value && value >= 0) {
            if (first && Digit(value)) throw new IOException("不支持这个 RTF 格式版本。");
            first = false;
            if (depth == 0) {
                if (value is not (9 or 10 or 13 or 32)) throw new IOException("RTF 文档结束后存在无效内容。");
                continue;
            }
            if (value == '{') { ++depth; continue; }
            if (value == '}') { --depth; continue; }
            if (value != '\\') continue;
            value = input.ReadByte();
            if (value < 0) throw new IOException("RTF 控制内容不完整。");
            if (value == '\'') {
                int a = input.ReadByte(), b = input.ReadByte();
                if (a < 0 || b < 0 || !Uri.IsHexDigit((char)a) || !Uri.IsHexDigit((char)b))
                    throw new IOException("RTF 字符转义无效。");
                continue;
            }
            if (!Letter(value)) continue;
            var word = new StringBuilder();
            do { word.Append((char)value); value = input.ReadByte(); } while (Letter(value));
            bool binary = word.ToString() == "bin";
            var parameter = new StringBuilder();
            if (value == '-') { parameter.Append('-'); value = input.ReadByte(); }
            while (Digit(value)) { if (binary) parameter.Append((char)value); value = input.ReadByte(); }
            if (value >= 0 && value != ' ') input.Seek(-1, SeekOrigin.Current);
            if (binary) {
                if (!long.TryParse(parameter.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long count) ||
                    count < 0 || count > input.Length - input.Position)
                    throw new IOException("RTF 二进制内容长度无效或不完整。");
                input.Seek(count, SeekOrigin.Current);
            }
        }
        if (depth != 0) throw new IOException("RTF 文档不完整，无法替换原文件。");
    }
    static bool Letter(int value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    static bool Digit(int value) => value is >= '0' and <= '9';
}
