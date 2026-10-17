using System.Text;

namespace Ragamuffin.Ingestion;

/// <summary>Makes extracted text safe to store: Postgres rejects NUL characters and unpaired UTF-16 surrogates.</summary>
public static class TextSanitizer
{
    public static string Clean(string text)
    {
        if (!NeedsCleaning(text)) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
                continue;
            }
            if (char.IsSurrogate(c)) { sb.Append('�'); continue; }
            if (c == '\0') continue;
            // Other control characters (form feeds, PDF glyph junk) become spaces; keep newlines and tabs.
            sb.Append(char.IsControl(c) && c is not '\n' and not '\t' ? ' ' : c);
        }
        return sb.ToString();
    }

    static bool NeedsCleaning(string text)
    {
        foreach (var c in text)
            if ((char.IsControl(c) && c is not '\n' and not '\t') || char.IsSurrogate(c))
                return true;
        return false;
    }
}
