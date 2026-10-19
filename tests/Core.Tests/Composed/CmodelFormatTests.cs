using System.IO.Compression;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using RfCore.Data;

namespace CircuitRF.Core.Tests.Composed;

/// <summary>brief-modelcomposer-1 R-mc1-1/R-mc1-2: the container is deterministic, and the reader
/// refuses by field.</summary>
public sealed class CmodelFormatTests
{
    [Fact]
    public void WriteReadWrite_IsByteIdentical()
    {
        var model = ComposedTestModels.Model() with
        {
            Symbol = "symbols/gan.csym",
            Arrays = new Dictionary<string, DataCube>
            {
                ["w1"] = new([new Axis("i", [0, 1, 2])], [1.5, -2.25, 3e-12]),
            },
        };

        byte[] first = Bytes(model);
        var read = CmodelFile.Read(new MemoryStream(first));
        byte[] second = Bytes(read);

        Assert.Equal(first, second);
        Assert.Equal([1.5, -2.25, 3e-12], read.Arrays["w1"].RealValues);
    }

    [Theory]
    [InlineData("\"Method\": \"Equation\"", "\"Method\": \"Spline\"", "Core.Constituents.Id.Method")]
    [InlineData("\"Fingers\": 4", "\"Fingers\": 0", "BasePeriphery.Fingers")]
    [InlineData("\"Inputs\": [\n          \"vgs\",\n          \"vds\"\n        ],\n        \"Expression\": \"beta",
                "\"Inputs\": [\n          \"vgs\",\n          \"T\"\n        ],\n        \"Expression\": \"beta",
                "Core.Constituents.Id.Inputs")]
    public void ABrokenField_IsRefusedByName(string from, string to, string field)
    {
        var ex = Assert.Throws<CmodelFormatException>(() => ReadEdited(from, to));
        Assert.Equal(field, ex.Field);
        Assert.StartsWith(field + ":", ex.Message);
    }

    [Fact]
    public void ANewerFormatVersion_IsRefused()
    {
        var ex = Assert.Throws<CmodelFormatException>(
            () => ReadEdited("\"FormatVersion\": 1", "\"FormatVersion\": 2"));
        Assert.Equal("FormatVersion", ex.Field);
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void AnUnknownScalingRule_IsRefusedByName()
    {
        var ex = Assert.Throws<CmodelFormatException>(
            () => ReadEdited("\"rg\": \"perFingerInverse\"", "\"rg\": \"quadratic\""));
        Assert.Equal("Network.Scaling.rg", ex.Field);
        Assert.Contains("'quadratic'", ex.Message);
    }

    private static byte[] Bytes(ComposedModel m)
    {
        using var s = new MemoryStream();
        CmodelFile.Write(m, s);
        return s.ToArray();
    }

    /// <summary>A valid model with one textual edit to its model.json, read back.</summary>
    private static ComposedModel ReadEdited(string from, string to)
    {
        string json = Encoding.UTF8.GetString(CmodelFile.ModelJson(ComposedTestModels.Model()));
        Assert.Contains(from, json);
        json = json.Replace(from, to);

        using var s = new MemoryStream();
        using (var zip = new ZipArchive(s, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var e = zip.CreateEntry(CmodelFile.ModelEntry).Open();
            e.Write(Encoding.UTF8.GetBytes(json));
        }
        s.Position = 0;
        return CmodelFile.Read(s);
    }
}
