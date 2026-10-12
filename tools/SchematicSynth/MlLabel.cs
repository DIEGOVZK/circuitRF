using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Schematic.Recognition;

namespace CircuitRF.SchematicSynth;

/// <summary>A point in the picture's own pixels, origin top-left, y down.</summary>
public readonly record struct Px(double X, double Y);

public sealed record LabelPin(string Name, Px At);

public sealed record LabelPart(
    string Id, string Class, string? Variant, int Turn, bool Mirror, double[] Body, IReadOnlyList<LabelPin> Pins,
    string? Designator, IReadOnlyList<string> Values, bool Cut);

public sealed record LabelWire(IReadOnlyList<Px> Points, double Pen);

public sealed record LabelText(string Id, Px[] Quad, string String, string Role);

public sealed record LabelAnnotation(double[] Box, string Kind);

/// <summary>
/// One picture's label in the IML-1 format (<c>tools/schematic-ml/schema/mllabel.schema.json</c>, version 1).
/// </summary>
public sealed record MlLabel(
    string Image, int Width, int Height, string Style,
    IReadOnlyList<LabelPart> Parts, IReadOnlyList<LabelWire> Wires, IReadOnlyList<Px> Junctions, IReadOnlyList<Px> Crossings,
    IReadOnlyList<LabelText> Texts, IReadOnlyList<LabelAnnotation> Annotations, IReadOnlyList<double[]> Ignore,
    IReadOnlyDictionary<string, string> Masks)
{
    /// <summary>The label as JSON: fixed key order and coordinates at a tenth of a pixel, so the same picture is the same
    /// bytes on every run.</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("image", Image);
            w.WriteNumber("width", Width);
            w.WriteNumber("height", Height);
            w.WriteString("origin", "synth");
            w.WriteNull("family");
            w.WriteString("style", Style);

            w.WriteStartArray("parts");
            foreach (var p in Parts)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("class", p.Class);
                if (p.Variant is { } v) w.WriteString("variant", v);
                w.WriteStartObject("orientation");
                w.WriteNumber("turn", p.Turn);
                w.WriteBoolean("mirror", p.Mirror);
                w.WriteEndObject();
                Box(w, "body", p.Body);
                w.WriteStartArray("pins");
                foreach (var q in p.Pins)
                {
                    w.WriteStartObject();
                    w.WriteString("name", q.Name);
                    Num(w, "x", q.At.X);
                    Num(w, "y", q.At.Y);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (p.Designator is { } d) w.WriteString("designator", d); else w.WriteNull("designator");
                w.WriteStartArray("values");
                foreach (var id in p.Values) w.WriteStringValue(id);
                w.WriteEndArray();
                if (p.Cut) w.WriteBoolean("cut", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("wires");
            foreach (var wire in Wires)
            {
                w.WriteStartObject();
                w.WriteStartArray("points");
                foreach (var pt in wire.Points) Point(w, pt);
                w.WriteEndArray();
                Num(w, "pen", Math.Max(0.1, wire.Pen));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("junctions");
            foreach (var j in Junctions) Point(w, j);
            w.WriteEndArray();
            w.WriteStartArray("crossings");
            foreach (var c in Crossings) Point(w, c);
            w.WriteEndArray();

            w.WriteStartArray("texts");
            foreach (var t in Texts)
            {
                w.WriteStartObject();
                w.WriteString("id", t.Id);
                w.WriteStartArray("quad");
                foreach (var q in t.Quad) Point(w, q);
                w.WriteEndArray();
                w.WriteString("string", t.String);
                w.WriteString("role", t.Role);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("annotations");
            foreach (var a in Annotations)
            {
                w.WriteStartObject();
                Box(w, "box", a.Box);
                w.WriteString("kind", a.Kind);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("ignore");
            foreach (var b in Ignore)
            {
                w.WriteStartArray();
                foreach (var v in b) w.WriteNumberValue(Round(v));
                w.WriteEndArray();
            }
            w.WriteEndArray();

            w.WriteStartObject("masks");
            foreach (var (k, v) in Masks) w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static double Round(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);
    private static void Num(Utf8JsonWriter w, string name, double v) => w.WriteNumber(name, Round(v));

    private static void Point(Utf8JsonWriter w, Px p)
    {
        w.WriteStartArray();
        w.WriteNumberValue(Round(p.X));
        w.WriteNumberValue(Round(p.Y));
        w.WriteEndArray();
    }

    private static void Box(Utf8JsonWriter w, string name, double[] b)
    {
        w.WriteStartArray(name);
        foreach (var v in b) w.WriteNumberValue(Round(v));
        w.WriteEndArray();
    }
}

/// <summary>
/// What a value text is under circuitRF's value grammar. circuitRF draws a shown parameter as <c>R = 50 Ohm</c>: the
/// grammar reads <c>R=50 Ohm</c> and <c>Z=50Ohm</c>, so the spaces round the <c>=</c> and before the unit are closed
/// up first. The text the label carries is the string as DRAWN — the text net learns to read what is on the screen;
/// IML-7's assembly closes the spaces up the same way before the grammar reads it.
/// </summary>
public static class ValueText
{
    private static readonly Regex AroundEquals = new(@"\s*=\s*", RegexOptions.CultureInvariant);
    private static readonly Regex BeforeUnit = new(@"(?<=\d)\s+(?=[A-Za-zΩµ°])", RegexOptions.CultureInvariant);

    public static string Closed(string text) => BeforeUnit.Replace(AroundEquals.Replace(text.Trim(), "="), "");

    /// <summary>Whether the grammar reads <paramref name="text"/> as a value or a set of line parameters, drawn as it is
    /// or with its spaces closed up.</summary>
    public static bool Reads(string text)
    {
        foreach (string t in new[] { text, AroundEquals.Replace(text.Trim(), "="), Closed(text) })
            if (ValueGrammar.Parse(t) is { Class: WordClass.Value or WordClass.LineParameters }) return true;
        return false;
    }

    /// <summary>Invariant formatting for numbers written into file names and messages.</summary>
    public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
