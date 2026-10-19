using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// A <c>.cmodel</c> the reader refuses. <see cref="Exception.Message"/> is one sentence that begins with
/// the FIELD it is about (<c>Core.Constituents.Id.Method: …</c>), which is what <c>check</c> prints and
/// what a placed <c>ComposedFet</c> reports (R-mc1-2).
/// </summary>
public sealed class CmodelFormatException(string field, string sentence)
    : Exception($"{field}: {sentence}")
{
    public string Field { get; } = field;
}

/// <summary>
/// Reads, validates and writes the <c>.cmodel</c> container (R-mc1-1, R-mc1-2).
///
/// <para><b>Deterministic.</b> <c>model.json</c> first, then <c>arrays/*.npy</c> in ordinal name
/// order; every entry carries the same fixed timestamp; JSON properties in a fixed order with
/// <c>\n</c> line ends; arrays through RfCore's own <c>.npy</c> writer. The same model writes the same
/// bytes, so a model can be diffed, hashed and committed.</para>
///
/// <para><b>The validator is the only one.</b> <see cref="Read(string)"/> calls <see cref="Validate"/>
/// before returning, and <c>check</c> calls <see cref="Read(string)"/> — so a file <c>check</c> passes
/// is a file a <c>ComposedFet</c> loads, and the sentence a refusal prints is the same in both.</para>
/// </summary>
public static class CmodelFile
{
    public const string Extension     = ".cmodel";
    public const string ModelEntry    = "model.json";
    public const string ArraysFolder  = "arrays/";

    /// <summary>The methods a constituent may name in this build. MC-2 adds Table and ANN.</summary>
    public static readonly IReadOnlyList<string> KnownMethods = [Constituent.EquationMethod];

    /// <summary>The trap models this build knows (D10). MC-10 adds the rest.</summary>
    public static readonly IReadOnlyList<string> KnownTrapModels = ["None"];

    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Reading ───────────────────────────────────────────────────────────────

    public static ComposedModel Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(fs);
    }

    public static ComposedModel Read(Stream stream)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException)
        {
            throw new CmodelFormatException("container", "the file is not a ZIP container.");
        }

        using (zip)
        {
            var entry = zip.GetEntry(ModelEntry)
                ?? throw new CmodelFormatException(ModelEntry, "the container holds no model.json.");

            JsonDocument doc;
            using (var s = entry.Open())
            {
                try { doc = JsonDocument.Parse(s); }
                catch (JsonException ex) { throw new CmodelFormatException(ModelEntry, $"it is not JSON ({ex.Message})."); }
            }

            ComposedModel model;
            using (doc) model = Parse(doc.RootElement);

            var arrays = new SortedDictionary<string, DataCube>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(ArraysFolder, StringComparison.Ordinal)) continue;
                string name = e.FullName[ArraysFolder.Length..];
                if (!name.EndsWith(".npy", StringComparison.Ordinal))
                    throw new CmodelFormatException(e.FullName, "an entry under arrays/ is not a .npy.");
                name = name[..^4];
                using var es = e.Open();
                using var buffer = new MemoryStream();
                es.CopyTo(buffer);
                buffer.Position = 0;
                DataSet ds;
                try { ds = DataSetImporter.ImportNpy(buffer); }
                catch (Exception ex) { throw new CmodelFormatException(e.FullName, $"the array does not read ({ex.Message})."); }
                if (!ds.Contains(name))
                    throw new CmodelFormatException(e.FullName, $"the .npy holds no cube named '{name}'.");
                arrays[name] = ds[name];
            }

            model = model with { Arrays = arrays };
            Validate(model);
            return model;
        }
    }

    private static ComposedModel Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new CmodelFormatException(ModelEntry, "the top level is not a JSON object.");

        int version = Int(root, "FormatVersion");
        if (version > ComposedModel.CurrentFormatVersion)
            throw new CmodelFormatException("FormatVersion",
                $"this file is format {version}, written by a newer circuitRF; this one reads up to " +
                $"format {ComposedModel.CurrentFormatVersion}.");
        if (version < 1)
            throw new CmodelFormatException("FormatVersion", $"{version} is not a format version.");

        foreach (string later in (string[])["Extrapolation", "Thermal"])
            if (root.TryGetProperty(later, out _))
                throw new CmodelFormatException(later, "this circuitRF does not read this field yet.");

        if (root.TryGetProperty("Traps", out var traps))
        {
            string trapModel = Str(traps, "Model", "Traps.Model");
            if (!KnownTrapModels.Contains(trapModel, StringComparer.Ordinal))
                throw new CmodelFormatException("Traps.Model",
                    $"'{trapModel}' is not a known trap model; the known models are {string.Join(", ", KnownTrapModels)}.");
        }

        var bp = Obj(root, "BasePeriphery");
        var t  = Obj(root, "Temperature");
        string mode = Str(t, "Mode", "Temperature.Mode");
        var temperature = mode switch
        {
            "Single" => ModelTemperature.Single(Num(t, "T0", "Temperature.T0")),
            "Range"  => ModelTemperature.Range(Num(t, "Tmin", "Temperature.Tmin"), Num(t, "Tmax", "Temperature.Tmax")),
            _ => throw new CmodelFormatException("Temperature.Mode", $"'{mode}' is not Single or Range."),
        };

        var net = Obj(root, "Network");
        var scaling = new List<ScalingRule>();
        if (net.TryGetProperty("Scaling", out var sc))
        {
            if (sc.ValueKind != JsonValueKind.Object)
                throw new CmodelFormatException("Network.Scaling", "it is not an object of variable → rule.");
            foreach (var p in sc.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    throw new CmodelFormatException($"Network.Scaling.{p.Name}", "the rule is not a name.");
                scaling.Add(new ScalingRule(p.Name, p.Value.GetString()!));
            }
        }

        var core = Obj(Obj(root, "Core"), "Constituents", "Core.Constituents");
        Constituent One(string name, bool optional = false)
        {
            string field = $"Core.Constituents.{name}";
            if (!core.TryGetProperty(name, out var c))
            {
                if (optional) return null!;
                throw new CmodelFormatException(field, "it is missing.");
            }
            return ParseConstituent(c, field);
        }

        Constituent? idRf = core.TryGetProperty("IdRF", out _) ? One("IdRF") : null;

        string? symbol = null;
        if (root.TryGetProperty("Symbol", out var sym))
        {
            if (sym.ValueKind != JsonValueKind.String)
                throw new CmodelFormatException("Symbol", "it is not a symbol name or a .csym path.");
            symbol = sym.GetString();
        }

        var provenance = new ModelProvenance();
        if (root.TryGetProperty("Provenance", out var pv))
        {
            var sources = new List<ModelSource>();
            if (pv.TryGetProperty("Sources", out var srcs))
                foreach (var s in srcs.EnumerateArray())
                    sources.Add(new ModelSource(Str(s, "Path", "Provenance.Sources.Path"),
                                                Str(s, "Sha256", "Provenance.Sources.Sha256")));
            provenance = new ModelProvenance
            {
                Created          = OptStr(pv, "Created"),
                CircuitRfVersion = OptStr(pv, "CircuitRfVersion"),
                Sources          = sources,
                SettingsDigest   = OptStr(pv, "SettingsDigest"),
            };
        }

        return new ComposedModel
        {
            FormatVersion = version,
            Kind          = Str(root, "Kind", "Kind"),
            Terminals     = Strings(root, "Terminals", "Terminals"),
            BasePeriphery = new BasePeriphery(Int(bp, "Fingers", "BasePeriphery.Fingers"),
                                              Str(bp, "UnitWidth", "BasePeriphery.UnitWidth")),
            Temperature   = temperature,
            Network       = new ExtrinsicNetwork(Str(net, "Netlist", "Network.Netlist"), scaling),
            Core          = new CoreConstituents(One("Id"), One("Ig"), One("Qg"), One("Qd"), idRf),
            Symbol        = symbol,
            Provenance    = provenance,
        };
    }

    private static Constituent ParseConstituent(JsonElement c, string field)
    {
        if (c.ValueKind != JsonValueKind.Object)
            throw new CmodelFormatException(field, "it is not an object.");

        var expressions = new List<KeyValuePair<string, string>>();
        var parameters  = new List<KeyValuePair<string, double>>();
        foreach (var p in c.EnumerateObject())
        {
            switch (p.Name)
            {
                case "Method":
                case "Inputs":
                    break;
                case "Parameters":
                    if (p.Value.ValueKind != JsonValueKind.Object)
                        throw new CmodelFormatException($"{field}.Parameters", "it is not an object of name → number.");
                    foreach (var q in p.Value.EnumerateObject())
                    {
                        if (q.Value.ValueKind != JsonValueKind.Number)
                            throw new CmodelFormatException($"{field}.Parameters.{q.Name}", "it is not a number.");
                        parameters.Add(new(q.Name, q.Value.GetDouble()));
                    }
                    break;
                default:
                    if (p.Value.ValueKind != JsonValueKind.String)
                        throw new CmodelFormatException($"{field}.{p.Name}", "it is not an expression.");
                    expressions.Add(new(p.Name, p.Value.GetString()!));
                    break;
            }
        }

        return new Constituent
        {
            Method      = Str(c, "Method", $"{field}.Method"),
            Inputs      = Strings(c, "Inputs", $"{field}.Inputs"),
            Expressions = expressions,
            Parameters  = parameters,
        };
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every rule a model must satisfy, refused by field. Called by <see cref="Read(string)"/> and by
    /// <see cref="Write(ComposedModel, Stream)"/>, so neither direction lets a bad model through.
    /// </summary>
    public static void Validate(ComposedModel m)
    {
        if (m.FormatVersion > ComposedModel.CurrentFormatVersion)
            throw new CmodelFormatException("FormatVersion",
                $"this model is format {m.FormatVersion}; this circuitRF reads up to format " +
                $"{ComposedModel.CurrentFormatVersion}.");
        if (m.Kind != ComposedModel.FetKind)
            throw new CmodelFormatException("Kind", $"'{m.Kind}' is not a known kind; the known kind is {ComposedModel.FetKind}.");
        if (!m.Terminals.SequenceEqual(["G", "D", "S"], StringComparer.Ordinal))
            throw new CmodelFormatException("Terminals", "a FET's terminals are G, D, S, in that order.");

        if (m.BasePeriphery.Fingers < 1)
            throw new CmodelFormatException("BasePeriphery.Fingers", $"{m.BasePeriphery.Fingers} is not a finger count.");
        if (UnitWidthMetres(m.BasePeriphery.UnitWidth) is not > 0)
            throw new CmodelFormatException("BasePeriphery.UnitWidth",
                $"'{m.BasePeriphery.UnitWidth}' is not a positive width with its unit (\"125 um\").");

        var t = m.Temperature;
        if (t.Mode == TemperatureMode.Single && !double.IsFinite(t.T0))
            throw new CmodelFormatException("Temperature.T0", "it is not a temperature.");
        if (t.Mode == TemperatureMode.Range && !(double.IsFinite(t.Tmin) && double.IsFinite(t.Tmax) && t.Tmin < t.Tmax))
            throw new CmodelFormatException("Temperature", "Tmin must be below Tmax.");

        foreach (var r in m.Network.Scaling)
            if (!PeripheryScaling.IsKnown(r.Rule))
                throw new CmodelFormatException($"Network.Scaling.{r.Variable}",
                    $"'{r.Rule}' is not a known scaling rule; the known rules are {string.Join(", ", PeripheryScaling.Known)}.");
        ComposedNetworkTemplate.Build(m.Network);

        var inputs = new HashSet<string>(StringComparer.Ordinal) { ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds };
        if (t.Mode == TemperatureMode.Range) inputs.Add(ComposedCoreProgram.T);

        ValidateConstituent(m.Core.Id, "Id", ["Expression"], inputs);
        ValidateConstituent(m.Core.Ig, "Ig", ["IgGs", "IgGd"], inputs);
        ValidateConstituent(m.Core.Qg, "Qg", ["Expression"], inputs);
        ValidateConstituent(m.Core.Qd, "Qd", ["Expression"], inputs);
        if (m.Core.IdRF is { } idRf) ValidateConstituent(idRf, "IdRF", ["Expression"], inputs);

        if (m.Symbol is { Length: 0 })
            throw new CmodelFormatException("Symbol", "it is empty; leave it out for the standard FET symbol.");
    }

    private static void ValidateConstituent(Constituent c, string name, string[] keys, HashSet<string> allowed)
    {
        string field = $"Core.Constituents.{name}";
        if (!KnownMethods.Contains(c.Method, StringComparer.Ordinal))
            throw new CmodelFormatException($"{field}.Method",
                $"'{c.Method}' is not a known method; the known methods are {string.Join(", ", KnownMethods)}.");

        foreach (string input in c.Inputs)
        {
            if (allowed.Contains(input)) continue;
            string why = input switch
            {
                ComposedCoreProgram.T => "a single-temperature model has no temperature input",
                "phi1" or "phi2"      => "trap states are inputs only of a model with traps",
                _                     => $"the inputs are {string.Join(", ", allowed)}",
            };
            throw new CmodelFormatException($"{field}.Inputs", $"'{input}' is not an input here: {why}.");
        }

        foreach (var e in c.Expressions)
            if (!keys.Contains(e.Key))
                throw new CmodelFormatException($"{field}.{e.Key}",
                    $"an {c.Method} {name} is {string.Join(" and ", keys)}; '{e.Key}' is not one of them.");

        var names = new HashSet<string>(c.Inputs, StringComparer.Ordinal);
        foreach (var p in c.Parameters)
        {
            if (!double.IsFinite(p.Value))
                throw new CmodelFormatException($"{field}.Parameters.{p.Key}", "it is not a finite number.");
            if (allowed.Contains(p.Key))
                throw new CmodelFormatException($"{field}.Parameters.{p.Key}", "a parameter cannot share an input's name.");
            names.Add(p.Key);
        }

        foreach (string key in keys)
        {
            string ef = $"{field}.{key}";
            string text = c.Expression(key) ?? throw new CmodelFormatException(ef, "it is missing.");
            Expr ast;
            try { ast = Parser.Parse(text); }
            catch (Exception ex) { throw new CmodelFormatException(ef, $"it does not parse: {ex.Message}"); }
            foreach (string r in AstWalker.CollectRefs(ast))
                if (!names.Contains(r) && r is not ("pi" or "e"))
                    throw new CmodelFormatException(ef,
                        $"'{r}' is neither one of its inputs ({string.Join(", ", c.Inputs)}) nor one of its parameters.");
        }
    }

    /// <summary>The unit width in metres, or null when the text is not a number and a length unit.</summary>
    public static double? UnitWidthMetres(string text)
    {
        var parts = text.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        if (parts.Length == 1) return null;
        return Units.Scale(parts[1]) is { } s && Units.BaseUnit(parts[1]) == "metre" ? v * s : null;
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    public static void Write(ComposedModel model, string path)
    {
        using var buffer = new MemoryStream();
        Write(model, buffer);
        File.WriteAllBytes(path, buffer.ToArray());
    }

    public static void Write(ComposedModel model, Stream stream)
    {
        Validate(model);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        var entry = zip.CreateEntry(ModelEntry, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        using (var s = entry.Open())
        {
            var bytes = ModelJson(model);
            s.Write(bytes, 0, bytes.Length);
        }

        foreach (var (name, cube) in model.Arrays.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            var e = zip.CreateEntry($"{ArraysFolder}{name}.npy", CompressionLevel.Optimal);
            e.LastWriteTime = EntryTime;
            var ds = new DataSet();
            ds.Add(name, cube);
            using var es = e.Open();
            DataSetExporter.ExportNpy(ds, es);
        }
    }

    /// <summary>The <c>model.json</c> bytes for <paramref name="m"/>, in the fixed order.</summary>
    public static byte[] ModelJson(ComposedModel m)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteNumber("FormatVersion", m.FormatVersion);
            w.WriteString("Kind", m.Kind);
            w.WriteStartArray("Terminals");
            foreach (string term in m.Terminals) w.WriteStringValue(term);
            w.WriteEndArray();

            w.WriteStartObject("BasePeriphery");
            w.WriteNumber("Fingers", m.BasePeriphery.Fingers);
            w.WriteString("UnitWidth", m.BasePeriphery.UnitWidth);
            w.WriteEndObject();

            w.WriteStartObject("Temperature");
            w.WriteString("Mode", m.Temperature.Mode.ToString());
            if (m.Temperature.Mode == TemperatureMode.Single)
                w.WriteNumber("T0", m.Temperature.T0);
            else
            {
                w.WriteNumber("Tmin", m.Temperature.Tmin);
                w.WriteNumber("Tmax", m.Temperature.Tmax);
            }
            w.WriteEndObject();

            w.WriteStartObject("Network");
            w.WriteString("Netlist", m.Network.Netlist);
            w.WriteStartObject("Scaling");
            foreach (var r in m.Network.Scaling) w.WriteString(r.Variable, r.Rule);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("Core");
            w.WriteStartObject("Constituents");
            WriteConstituent(w, "Id", m.Core.Id);
            WriteConstituent(w, "Ig", m.Core.Ig);
            WriteConstituent(w, "Qg", m.Core.Qg);
            WriteConstituent(w, "Qd", m.Core.Qd);
            if (m.Core.IdRF is { } idRf) WriteConstituent(w, "IdRF", idRf);
            w.WriteEndObject();
            w.WriteEndObject();

            if (m.Symbol is { } symbol) w.WriteString("Symbol", symbol);

            w.WriteStartObject("Provenance");
            w.WriteString("Created", m.Provenance.Created);
            w.WriteString("CircuitRfVersion", m.Provenance.CircuitRfVersion);
            w.WriteStartArray("Sources");
            foreach (var s in m.Provenance.Sources)
            {
                w.WriteStartObject();
                w.WriteString("Path", s.Path);
                w.WriteString("Sha256", s.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("SettingsDigest", m.Provenance.SettingsDigest);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static void WriteConstituent(Utf8JsonWriter w, string name, Constituent c)
    {
        w.WriteStartObject(name);
        w.WriteString("Method", c.Method);
        w.WriteStartArray("Inputs");
        foreach (string i in c.Inputs) w.WriteStringValue(i);
        w.WriteEndArray();
        foreach (var e in c.Expressions) w.WriteString(e.Key, e.Value);
        w.WriteStartObject("Parameters");
        foreach (var p in c.Parameters) w.WriteNumber(p.Key, p.Value);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    // ── JSON field helpers: every miss names its field ───────────────────────

    private static JsonElement Obj(JsonElement e, string name, string? field = null)
    {
        field ??= name;
        if (!e.TryGetProperty(name, out var v)) throw new CmodelFormatException(field, "it is missing.");
        if (v.ValueKind != JsonValueKind.Object) throw new CmodelFormatException(field, "it is not an object.");
        return v;
    }

    private static string Str(JsonElement e, string name, string field)
    {
        if (!e.TryGetProperty(name, out var v)) throw new CmodelFormatException(field, "it is missing.");
        if (v.ValueKind != JsonValueKind.String) throw new CmodelFormatException(field, "it is not a string.");
        return v.GetString()!;
    }

    private static string OptStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static double Num(JsonElement e, string name, string field)
    {
        if (!e.TryGetProperty(name, out var v)) throw new CmodelFormatException(field, "it is missing.");
        if (v.ValueKind != JsonValueKind.Number) throw new CmodelFormatException(field, "it is not a number.");
        return v.GetDouble();
    }

    private static int Int(JsonElement e, string name, string? field = null)
    {
        field ??= name;
        double d = Num(e, name, field);
        if (d != Math.Floor(d) || d > int.MaxValue || d < int.MinValue)
            throw new CmodelFormatException(field, $"{d.ToString(CultureInfo.InvariantCulture)} is not a whole number.");
        return (int)d;
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name, string field)
    {
        if (!e.TryGetProperty(name, out var v)) throw new CmodelFormatException(field, "it is missing.");
        if (v.ValueKind != JsonValueKind.Array) throw new CmodelFormatException(field, "it is not a list.");
        var list = new List<string>();
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String) throw new CmodelFormatException(field, "an entry is not a string.");
            list.Add(x.GetString()!);
        }
        return list;
    }
}
