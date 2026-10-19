// EDIF string escapes — brief-schimp-3 §2.
//
// A character outside printable ASCII is written as `%<decimal>%` — one or more space-separated byte values between two
// percent signs (`%13 10%`), and an exporter may also write each byte as its own escape (`%13%%10%`). Adjacent escapes are
// ONE RUN, and a run is ONE byte string in the exporting machine's ANSI code page: decoding byte by byte would split every
// multi-byte character. The code page is not stated in the file, so a run is tried as UTF-8 first, then as the code page
// the caller names (1252 by default), and a run neither decodes falls back to Latin-1, which keeps every byte. The counts
// say which decoded what; nothing is lost silently.
//
// Within a run: CR LF, a lone CR and a lone LF are each one newline; every other C0 control is dropped. A newline that ends
// the whole string is dropped.

using System.Text;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>Decodes EDIF strings for one read, counting how each non-ASCII run was decoded.</summary>
public sealed class EdifText
{
    /// <summary>The ANSI code page tried after UTF-8 when none is given.</summary>
    public const int DefaultCodePage = 1252;

    private readonly Encoding? _codePage;
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public EdifText(int codePage = DefaultCodePage)
    {
        CodePage = codePage;
        _codePage = StrictEncoding(codePage);
    }

    public int CodePage { get; }

    /// <summary>False when <see cref="CodePage"/> is not one this runtime knows; every run it would have decoded is Latin-1.</summary>
    public bool CodePageKnown => _codePage is not null;

    /// <summary>Non-ASCII runs decoded as UTF-8, as <see cref="CodePage"/>, and as Latin-1 (neither decoded them).</summary>
    public int Utf8Runs => _utf8;
    public int CodePageRuns => _cp;
    public int Latin1Runs => _latin1;

    private int _utf8, _cp, _latin1;

    /// <summary>One sentence per way a non-ASCII run was decoded, for the reader's notes.</summary>
    public IEnumerable<string> Notes()
    {
        if (Utf8Runs > 0) yield return $"{Utf8Runs} escaped text run(s) decoded as UTF-8.";
        if (CodePageRuns > 0) yield return $"{CodePageRuns} escaped text run(s) decoded as code page {CodePage} (not valid UTF-8).";
        if (Latin1Runs > 0)
            yield return $"{Latin1Runs} escaped text run(s) decoded as Latin-1: "
                       + (CodePageKnown ? $"neither UTF-8 nor code page {CodePage} decodes them." : $"code page {CodePage} is not known here.")
                       + " Every byte is kept; choose another code page if the text reads wrong.";
    }

    /// <summary>The text of a raw EDIF string: its escapes decoded. A <c>%</c> that does not open a well-formed escape
    /// is kept as written.</summary>
    public string Decode(string raw)
    {
        if (raw.IndexOf('%') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        var run = new List<byte>();
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '%' && TryEscape(raw, i, run, out int next))
            {
                i = next;
                while (i < raw.Length && raw[i] == '%' && TryEscape(raw, i, run, out next)) i = next;
                AppendRun(sb, run);
                run.Clear();
                continue;
            }
            sb.Append(raw[i++]);
        }
        if (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
        return sb.ToString();
    }

    /// <summary>Bytes from outside any escape (a raw non-ASCII file) decoded by the same rule, without the C0 handling.</summary>
    internal string DecodeBytes(byte[] bytes, out string how)
    {
        if (_codePage is not null)
            try { how = $"code page {CodePage}"; return _codePage.GetString(bytes); }
            catch (DecoderFallbackException) { }
        how = "Latin-1";
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>One <c>%d d …%</c> escape at <paramref name="at"/>, its bytes appended to <paramref name="run"/>.</summary>
    private static bool TryEscape(string s, int at, List<byte> run, out int next)
    {
        next = at;
        int close = s.IndexOf('%', at + 1);
        if (close < 0) return false;
        var body = s.AsSpan(at + 1, close - at - 1);
        if (body.Trim().IsEmpty) return false;
        var bytes = new List<byte>();
        foreach (var part in body.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > 3 || !part.All(char.IsAsciiDigit)) return false;
            int v = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            if (v > 255) return false;
            bytes.Add((byte)v);
        }
        run.AddRange(bytes);
        next = close + 1;
        return true;
    }

    private void AppendRun(StringBuilder sb, List<byte> run)
    {
        string text;
        byte[] bytes = [.. run];
        if (bytes.All(b => b < 0x80)) text = Encoding.ASCII.GetString(bytes);
        else
        {
            text = TryDecode(StrictUtf8, bytes) is { } u ? Counted(u, ref _utf8)
                 : _codePage is not null && TryDecode(_codePage, bytes) is { } c ? Counted(c, ref _cp)
                 : Counted(Encoding.Latin1.GetString(bytes), ref _latin1);
        }
        for (int k = 0; k < text.Length; k++)
        {
            char ch = text[k];
            if (ch == '\r') { sb.Append('\n'); if (k + 1 < text.Length && text[k + 1] == '\n') k++; }
            else if (ch == '\n') sb.Append('\n');
            else if (ch >= ' ') sb.Append(ch);
        }
    }

    private static string Counted(string s, ref int counter) { counter++; return s; }

    private static string? TryDecode(Encoding e, byte[] bytes)
    {
        try { return e.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>The code page with an exception fallback, so an undefined byte is a failed decode and not a '?'.</summary>
    private static Encoding? StrictEncoding(int codePage)
    {
        Encoding? e = CodePagesEncodingProvider.Instance.GetEncoding(codePage);
        if (e is null)
            try { e = Encoding.GetEncoding(codePage); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
        var strict = (Encoding)e.Clone();
        strict.DecoderFallback = DecoderFallback.ExceptionFallback;
        return strict;
    }
}
