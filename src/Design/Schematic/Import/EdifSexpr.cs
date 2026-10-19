// The EDIF 2.0.0 tokenizer — brief-schimp-3 R-si3-1.
//
// ── Why this is not PcbSexpr ──────────────────────────────────────────────────────────────────────
//
// MEASURED against PcbSexpr's own rules, not argued: two of EDIF's are not the board format's, and both live where a
// tokenizer cannot be parameterised.
//
//   * A backslash is an ordinary character in an EDIF string. PcbSexpr reads `\x` as an escape for `x`, so a library
//     path such as "C:\lib\parts.lib" reaches the reader as "C:libparts.lib" — silently. EDIF escapes with `%<decimal>%`
//     instead (EdifText), and a double quote inside a string is spelled `%34%`, so a string simply ends at the next `"`.
//   * EDIF keywords are case-insensitive (`(edif`, `(EDIF`, `(pointList`, `(POINTLIST`); PcbSexpr keeps the tag as
//     written and every Child("…") lookup is ordinal. Tags are lowercased HERE, once, so the reader names each keyword
//     one way. Atoms and strings keep their case: identifiers are compared case-insensitively by the reader, and text is
//     the user's.
//
// What IS shared is the node: PcbNode, with its Child/Atom/Num helpers. `&`-prefixed identifiers and
// `(rename id "text")` pairs need nothing special — an identifier is an atom and a rename is a list.
//
// Iterative, for PcbSexpr's reason: a truncated file is a run of '(' and recursion would turn it into a StackOverflow.

using System.Text;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Design.Schematic.Import;

public static class EdifSexpr
{
    /// <summary>Parses EDIF text into <see cref="PcbNode"/>s with lowercased tags. Strings are returned RAW — their
    /// <c>%…%</c> escapes undecoded, because how a run decodes depends on a code page only the reader knows.</summary>
    public static PcbSexpr.ParseResult Parse(string text)
    {
        var diagnostics = new List<string>();
        var stack = new List<PcbNode>();
        PcbNode? root = null;
        int i = 0, n = text.Length;

        while (i < n)
        {
            char c = text[i];
            if (c == '(')
            {
                i++;
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                int tagStart = i;
                while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '(' && text[i] != ')' && text[i] != '"') i++;
                var node = new PcbNode { Tag = text[tagStart..i].ToLowerInvariant() };
                if (stack.Count > 0) stack[^1].Items.Add(node);
                else if (root is null) root = node;
                else diagnostics.Add("More than one top-level list in the file — only the first was read.");
                stack.Add(node);
                continue;
            }
            if (c == ')')
            {
                i++;
                if (stack.Count == 0) { diagnostics.Add("Unbalanced ')' in the file — ignored."); continue; }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            if (c == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) { diagnostics.Add("A quoted string is unterminated at end of file — read to the end."); end = n; }
                if (stack.Count > 0) stack[^1].Items.Add(text[(i + 1)..end]);
                i = Math.Min(n, end + 1);
                continue;
            }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            int start = i;
            while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '(' && text[i] != ')' && text[i] != '"') i++;
            if (stack.Count > 0) stack[^1].Items.Add(text[start..i]);
            else diagnostics.Add($"Atom \"{text[start..i]}\" outside any list — ignored.");
        }
        if (stack.Count > 0)
            diagnostics.Add($"The file ends inside {stack.Count} unclosed list(s) — everything that closed was read.");
        return new PcbSexpr.ParseResult(root, diagnostics);
    }

    /// <summary>The file's bytes as text: UTF-8 when they are valid UTF-8, else the code page's, else Latin-1 — every byte
    /// kept. EDIF itself is ASCII; anything else came from the exporting machine.</summary>
    internal static string TextOf(byte[] bytes, EdifText decoder)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return decoder.DecodeBytes(bytes, out _); }
    }
}
