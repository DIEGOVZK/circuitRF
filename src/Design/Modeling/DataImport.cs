// Every importer a data set can name (brief-modelcomposer-3 R-mc3-3), each landing in NormalisedData:
//
//   touchstone  one file per bias point; the bias read from the file name or its comment lines by the data
//               set's BiasPattern. Point id: the source path as the .cmc states it.
//   csv         CSV or TSV with a column map. A cell may carry its own unit ("12 mA"), read by the value+unit
//               conventions every circuitRF field uses; otherwise the column's unit applies.
//               Point id: "<source>:<line>".
//   npy / mat   named arrays — circuitRF's own .npy through NpyReader (DataSetImporter), a .mat through
//               MatReader. Bias arrays are either one value per point, or the two axes of a grid whose
//               current and table arrays are shaped [vgs, vds] (or [vds, vgs]). A Y table converts to S at
//               the import's Z0, which is exact and invertible. Point id: "<source>#<k>".
//
// An importer REFUSES with the source it is about and why — never a guessed bias, never a skipped row.
// Each returns one summary line: points, bias range, frequency range.

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Optimization;
using CircuitRF.Engine;
using RfCore;
using RfCore.Export;

namespace CircuitRF.Design.Modeling;

/// <summary>An import that cannot proceed. The message begins with the source it is about.</summary>
public sealed class DataImportException(string source, string sentence) : Exception($"{source}: {sentence}")
{
    public string SourceName { get; } = source;
}

/// <summary>What an import produced, and its one-line report.</summary>
public sealed record ImportResult(NormalisedData Data, string Summary);

public static class DataImport
{
    /// <summary>
    /// Imports <paramref name="set"/>, its sources resolved against <paramref name="cmcDirectory"/>. Progress
    /// is per file; cancellation is checked between files and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static ImportResult Import(CmcDataSet set, string cmcDirectory, RunControl? rc = null)
    {
        rc?.BeginStage($"Importing {set.Name}", set.Sources.Count, "file(s)");
        var data = set.Import.Format switch
        {
            ImportSettings.Touchstone => TouchstoneSet(set, cmcDirectory, rc),
            ImportSettings.Csv        => Csv(set, cmcDirectory, rc),
            ImportSettings.Npy or ImportSettings.Mat => Arrays(set, cmcDirectory, rc),
            _ => throw new DataImportException(set.Name, $"'{set.Import.Format}' is not an import format."),
        };
        return new ImportResult(data, Summary(data));
    }

    public static string ResolveSource(string cmcDirectory, string source) =>
        Path.GetFullPath(Path.Combine(cmcDirectory, source.Replace('/', Path.DirectorySeparatorChar)));

    // ── Touchstone sets ───────────────────────────────────────────────────────

    private static NormalisedData TouchstoneSet(CmcDataSet set, string dir, RunControl? rc)
    {
        var im = set.Import;
        var pattern = im.Pattern is null ? null : BiasPattern.Parse(im.Pattern);
        bool byName = im.PatternSource == "name";
        bool passive = set.Kind == DataSetKind.PassiveSParams;

        var points = new List<BiasPoint>();
        var s = new List<Complex>();
        double[]? freqs = null;
        double z0 = double.NaN;
        int ports = 0;

        foreach (string src in set.Sources)
        {
            rc?.ThrowIfCancellationRequested();
            string path = ResolveSource(dir, src);
            if (!File.Exists(path)) throw new DataImportException(src, "the file does not exist.");
            SNP snp;
            try { snp = TouchstoneIO.ReadFile(path, readComments: true); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new DataImportException(src, $"it is not a readable Touchstone file ({ex.Message})."); }

            if (snp.IsEmpty) throw new DataImportException(src, "it holds no frequencies.");
            if (snp.Type != MatrixType.S) throw new DataImportException(src, $"it holds {snp.Type}-parameters; a data set reads S.");
            if (!passive && snp.Ports != 2) throw new DataImportException(src, $"it is a {snp.Ports}-port; a FET's data is a 2-port.");
            if (ports == 0) ports = snp.Ports;
            else if (snp.Ports != ports) throw new DataImportException(src, $"it is a {snp.Ports}-port and the set's first file a {ports}-port.");
            if (snp.Z0.Imaginary != 0 || snp.Z0PerPort is not null)
                throw new DataImportException(src, "its reference impedance is not one real value.");
            if (double.IsNaN(z0)) z0 = snp.Z0.Real;
            else if (snp.Z0.Real != z0) throw new DataImportException(src, $"its Z0 is {G(snp.Z0.Real)} Ω and the set's first file {G(z0)} Ω.");
            if (freqs is null) freqs = snp.Frequencies;
            else if (!SameAxis(freqs, snp.Frequencies))
                throw new DataImportException(src, "its frequencies differ from the set's first file; one data set shares one frequency axis.");

            IReadOnlyDictionary<string, double> f = new Dictionary<string, double>();
            if (pattern is not null)
            {
                string text = byName
                    ? Path.GetFileNameWithoutExtension(path)
                    : string.Join(" ", snp.Comments.Where(c => c.FrequencyIndex < 0).Select(c => c.Text));
                f = pattern.Match(text, whole: byName)
                    ?? throw new DataImportException(src, byName
                           ? $"the file name does not match the pattern '{im.Pattern}'."
                           : $"no comment line matches the pattern '{im.Pattern}'.");
            }
            points.Add(new BiasPoint(src, Get(f, "vgs"), Get(f, "vds"), Get(f, "ig"), Get(f, "id"))
            {
                AmbientC = Get(f, "T"), Geometry = Get(f, "geometry"),
            });
            foreach (var m in snp.Matrices)
                for (int i = 0; i < ports; i++)
                    for (int j = 0; j < ports; j++)
                        s.Add(m[i, j]);
            rc?.TickStage();
        }
        return new NormalisedData { Points = points, Ports = ports, FrequenciesHz = freqs, S = [.. s], Z0 = z0 };
    }

    private static double Get(IReadOnlyDictionary<string, double> f, string name) => f.TryGetValue(name, out var v) ? v : double.NaN;

    private static bool SameAxis(double[] a, double[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-9 * Math.Max(Math.Abs(a[i]), 1)) return false;
        return true;
    }

    // ── CSV / TSV ─────────────────────────────────────────────────────────────

    private static NormalisedData Csv(CmcDataSet set, string dir, RunControl? rc)
    {
        var points = new List<BiasPoint>();
        foreach (var (id, v) in CsvRows(set, dir, rc))
            points.Add(new BiasPoint(id, v["vgs"], v["vds"], Get(v, "ig"), Get(v, "id"))
            {
                AmbientC = Get(v, "T"), VgsIntrinsic = Get(v, "vgsi"), VdsIntrinsic = Get(v, "vdsi"),
            });
        return new NormalisedData { Points = points };
    }

    /// <summary>
    /// Every data row of a CSV/TSV set's sources, in order: its stable id (<c>source:line</c>) and each mapped quantity's
    /// value, scaled by its unit. The delimiter is inferred from the first row (tab, then <c>;</c> without <c>,</c>, then
    /// <c>,</c>) unless the import states one. A large-signal set (MC-11) reads its columns through here too.
    /// </summary>
    internal static List<(string Id, Dictionary<string, double> Values)> CsvRows(CmcDataSet set, string dir, RunControl? rc)
    {
        var im = set.Import;
        var rows = new List<(string, Dictionary<string, double>)>();
        foreach (string src in set.Sources)
        {
            rc?.ThrowIfCancellationRequested();
            string path = ResolveSource(dir, src);
            if (!File.Exists(path)) throw new DataImportException(src, "the file does not exist.");
            var lines = File.ReadAllLines(path);

            char delimiter = '\0';
            int[]? index = null;
            for (int ln = 0; ln < lines.Length; ln++)
            {
                string line = lines[ln].Trim();
                if (line.Length == 0 || line[0] is '#' or '!' or '%') continue;
                if (delimiter == '\0')
                    delimiter = im.Delimiter is { Length: > 0 } d ? (d == "\\t" ? '\t' : d[0])
                              : line.Contains('\t') ? '\t' : line.Contains(';') && !line.Contains(',') ? ';' : ',';
                var cells = line.Split(delimiter).Select(c => c.Trim().Trim('"')).ToArray();

                if (index is null)
                {
                    bool header = im.Header ?? true;
                    index = [.. im.Columns!.Select(c => ColumnIndex(c, header ? cells : null, src))];
                    if (header) continue;
                }

                var v = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int k = 0; k < index.Length; k++)
                {
                    var col = im.Columns![k];
                    string where = $"{src}:{ln + 1}";
                    if (index[k] >= cells.Length) throw new DataImportException(where, $"the row has no column '{col.Column}'.");
                    v[col.Quantity] = Cell(cells[index[k]], col, where);
                }
                rows.Add(($"{src}:{ln + 1}", v));
            }
            rc?.TickStage();
        }
        return rows;
    }

    private static int ColumnIndex(ColumnMap c, string[]? header, string src)
    {
        if (header is null)
        {
            if (int.TryParse(c.Column, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1) return n - 1;
            throw new DataImportException(src, $"without a header, column '{c.Column}' must be a column number from 1.");
        }
        int i = Array.FindIndex(header, h => string.Equals(h, c.Column, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : throw new DataImportException(src, $"the header has no column '{c.Column}'.");
    }

    private static double Cell(string text, ColumnMap col, string where)
    {
        if (!TunableValue.TryParse(text, out double number, out string unit, out double si))
            throw new DataImportException(where, $"'{text}' in column '{col.Column}' is not a number.");
        if (unit.Length > 0) return si;
        return number * (col.Unit is { } u ? CmcFile.UnitScale(u) ?? 1 : 1);
    }

    // ── .npy / .mat arrays ───────────────────────────────────────────────────

    private sealed record NamedArray(int[] Shape, double[] Re, double[]? Im)
    {
        public int Count => Re.Length;
        public Complex At(int i) => new(Re[i], Im is null ? 0 : Im[i]);
    }

    private static NormalisedData Arrays(CmcDataSet set, string dir, RunControl? rc)
    {
        var im = set.Import;
        var map = im.Arrays!;
        var points = new List<BiasPoint>();
        var s = new List<Complex>();
        var cTable = new List<double>();
        double[]? freqs = null;
        double z0 = im.Z0 ?? 50;

        foreach (string src in set.Sources)
        {
            rc?.ThrowIfCancellationRequested();
            string path = ResolveSource(dir, src);
            if (!File.Exists(path)) throw new DataImportException(src, "the file does not exist.");
            var arrays = Load(path, src, im.Format, map.Values.ToArray());
            NamedArray? A(string q) => map.TryGetValue(q, out var name)
                ? arrays.TryGetValue(name, out var a) ? a : throw new DataImportException(src, $"it holds no array '{name}' (mapped to {q}).")
                : null;

            var vgs = A("vgs")!; var vds = A("vds")!;
            var table = A("S") ?? A("Y");
            bool isY = !map.ContainsKey("S") && map.ContainsKey("Y");
            var cArr = A("C");
            var currentLike = A("id") ?? A("ig") ?? table ?? cArr;

            // One value per point, or a grid of vgs × vds.
            int n; bool grid = false, vdsSlow = false;
            if (vgs.Count == vds.Count && (currentLike is null || LeadingCount(currentLike, vgs.Count)))
                n = vgs.Count;
            else if (currentLike is not null && LeadingCount(currentLike, vgs.Count * vds.Count))
            {
                n = vgs.Count * vds.Count; grid = true;
                vdsSlow = currentLike.Shape.Length >= 2 && currentLike.Shape[0] == vds.Count && currentLike.Shape[1] == vgs.Count && vgs.Count != vds.Count;
            }
            else throw new DataImportException(src, $"vgs ({vgs.Count}) and vds ({vds.Count}) are neither one value per point nor the axes of the other arrays' grid.");

            double Bias(NamedArray a, bool isVgs, int k)
            {
                if (!grid) return a.Re[k];
                int slow = k / (vdsSlow ? vgs.Count : vds.Count), fast = k % (vdsSlow ? vgs.Count : vds.Count);
                return a.Re[isVgs ^ vdsSlow ? slow : fast];
            }
            double[] PerPoint(string q)
            {
                var a = A(q);
                if (a is null) return [.. Enumerable.Repeat(double.NaN, n)];
                if (a.Count != n) throw new DataImportException(src, $"'{map[q]}' holds {a.Count} values for {n} points.");
                return a.Re;
            }
            var ig = PerPoint("ig"); var id = PerPoint("id"); var t = PerPoint("T");
            var vgsi = PerPoint("vgsi"); var vdsi = PerPoint("vdsi");

            if (table is not null)
            {
                double[] f = A("freq")?.Re ?? (im.FrequencyHz is { } fh ? [fh] : throw new DataImportException(src,
                    $"the {(isY ? "Y" : "S")} table has no frequency: map a freq array, or state FrequencyHz."));
                if (freqs is null) freqs = f;
                else if (!SameAxis(freqs, f)) throw new DataImportException(src, "its frequencies differ from the set's first source.");
                if (table.Count != n * f.Length * 4)
                    throw new DataImportException(src, $"'{map[isY ? "Y" : "S"]}' holds {table.Count} values for {n} points × {f.Length} frequencies × 2 × 2.");
                for (int k = 0; k < n * f.Length; k++)
                {
                    Complex a = table.At(4 * k), b = table.At(4 * k + 1), c = table.At(4 * k + 2), d = table.At(4 * k + 3);
                    if (isY) (a, b, c, d) = YToS(a * z0, b * z0, c * z0, d * z0);
                    s.Add(a); s.Add(b); s.Add(c); s.Add(d);
                }
            }
            if (cArr is not null)
            {
                if (cArr.Count != n * 4) throw new DataImportException(src, $"'{map["C"]}' holds {cArr.Count} values for {n} points × 2 × 2.");
                cTable.AddRange(cArr.Re);
            }
            for (int k = 0; k < n; k++)
                points.Add(new BiasPoint($"{src}#{k}", Bias(vgs, true, k), Bias(vds, false, k), ig[k], id[k])
                {
                    AmbientC = t[k], VgsIntrinsic = vgsi[k], VdsIntrinsic = vdsi[k],
                });
            rc?.TickStage();
        }
        return new NormalisedData
        {
            Points = points,
            FrequenciesHz = freqs,
            S = s.Count > 0 ? [.. s] : null,
            Z0 = z0,
            C = cTable.Count > 0 ? [.. cTable] : null,
        };
    }

    /// <summary>True when <paramref name="a"/> holds <paramref name="n"/> points — its whole count, or its leading dimensions' product.</summary>
    private static bool LeadingCount(NamedArray a, int n)
    {
        if (a.Count == n) return true;
        int prod = 1;
        foreach (int d in a.Shape) { prod *= d; if (prod == n) return true; if (prod > n) break; }
        return false;
    }

    /// <summary>S from normalised y = Z0·Y of a 2-port: S = (I − y)(I + y)⁻¹.</summary>
    private static (Complex, Complex, Complex, Complex) YToS(Complex y11, Complex y12, Complex y21, Complex y22)
    {
        Complex p11 = 1 + y11, p12 = y12, p21 = y21, p22 = 1 + y22;
        Complex det = p11 * p22 - p12 * p21;
        Complex i11 = p22 / det, i12 = -p12 / det, i21 = -p21 / det, i22 = p11 / det;
        Complex m11 = 1 - y11, m12 = -y12, m21 = -y21, m22 = 1 - y22;
        return (m11 * i11 + m12 * i21, m11 * i12 + m12 * i22, m21 * i11 + m22 * i21, m21 * i12 + m22 * i22);
    }

    private static Dictionary<string, NamedArray> Load(string path, string src, string format, string[] names)
    {
        var result = new Dictionary<string, NamedArray>(StringComparer.Ordinal);
        try
        {
            if (format == ImportSettings.Mat)
            {
                foreach (var (name, a) in MatReader.Read(path, names))
                    result[name] = new NamedArray(a.Shape, a.Real, a.Imag);
                return result;
            }
            var ds = DataSetImporter.Import(path).DataSet;
            foreach (string name in names)
            {
                if (!ds.Contains(name)) continue;
                var cube = ds[name];
                int[] shape = [.. cube.Axes.Select(x => x.Length)];
                result[name] = cube.DataKind == RfCore.Data.DataKind.Complex
                    ? new NamedArray(shape, [.. cube.ComplexValues.Select(c => c.Real)], [.. cube.ComplexValues.Select(c => c.Imaginary)])
                    : new NamedArray(shape, cube.RealValues, null);
            }
            return result;
        }
        catch (MatFormatException ex) { throw new DataImportException(src, ex.Message); }
        catch (InvalidDataException ex) { throw new DataImportException(src, ex.Message); }
    }

    // ── The one-line report ──────────────────────────────────────────────────

    public static string Summary(NormalisedData d)
    {
        var parts = new List<string> { $"{d.Count} point(s)" };
        var vgs = d.Points.Select(p => p.Vgs).Where(double.IsFinite).ToArray();
        var vds = d.Points.Select(p => p.Vds).Where(double.IsFinite).ToArray();
        if (vgs.Length > 0) parts.Add($"Vgs {G(vgs.Min())} to {G(vgs.Max())} V");
        if (vds.Length > 0) parts.Add($"Vds {G(vds.Min())} to {G(vds.Max())} V");
        if (d.FrequenciesHz is { Length: > 0 } f)
            parts.Add($"{G(f[0] / 1e9)} to {G(f[^1] / 1e9)} GHz in {f.Length} frequencies");
        return string.Join(", ", parts) + ".";
    }

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
