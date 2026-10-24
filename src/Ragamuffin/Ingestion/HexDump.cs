using System.Text;

namespace Ragamuffin.Ingestion;

/// <summary>
/// Turns binary files into searchable text: one line per 16 bytes with the offset, the bytes in hex, and their
/// printable ASCII (like <c>xxd</c>). Byte patterns (<c>a9 00 8d</c>) and embedded strings (<c>HELLO</c>) can then be found
/// by full-text search, and Claude can read the bytes directly. The original file is kept for download.
/// </summary>
public static class HexDump
{
    public const int BytesPerLine = 16;

    public static string Format(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder((bytes.Length / BytesPerLine + 1) * 78);
        for (var offset = 0; offset < bytes.Length; offset += BytesPerLine)
        {
            var line = bytes.Slice(offset, Math.Min(BytesPerLine, bytes.Length - offset));
            sb.Append(offset.ToString("x8")).Append(": ");
            for (var i = 0; i < BytesPerLine; i++)
            {
                if (i == 8) sb.Append(' ');
                sb.Append(i < line.Length ? line[i].ToString("x2") + " " : "   ");
            }
            sb.Append(" |");
            foreach (var b in line) sb.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
            sb.Append("|\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The byte offset a hex-dump line (1-based) starts at.</summary>
    public static long OffsetOfLine(int line) => (long)(line - 1) * BytesPerLine;
}
