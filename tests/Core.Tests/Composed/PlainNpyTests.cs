using System.IO.Compression;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using RfCore.Data;

namespace CircuitRF.Core.Tests.Composed;

/// <summary>A <c>.cmodel</c> written by a script of its own may hold its arrays as plain <c>.npy</c> files — what
/// <c>np.save</c> or a MATLAB writer produces — and a table then names its sites in <c>&lt;name&gt;.&lt;input&gt;</c>
/// lists.</summary>
public sealed class PlainNpyTests
{
    [Fact]
    public void PlainArrays_AreRead_InEitherOrder_AsFloat64OrFloat32()
    {
        using var s = new MemoryStream();
        CmodelFile.Write(ComposedTestModels.Model(), s);
        using (var zip = new ZipArchive(s, ZipArchiveMode.Update, leaveOpen: true))
        {
            Add(zip, "c", Npy("<f8", false, "(2, 3)", F8(1, 2, 3, 4, 5, 6)));
            Add(zip, "f", Npy("<f8", true,  "(2, 3)", F8(1, 4, 2, 5, 3, 6)));   // the same matrix, column-major
            Add(zip, "s", Npy("<f8", false, "()",     F8(0.25)));
            Add(zip, "h", Npy("<f4", false, "(2,)",   [.. BitConverter.GetBytes(1.5f), .. BitConverter.GetBytes(-2f)]));
        }
        s.Position = 0;
        var arrays = CmodelFile.Read(s).Arrays;

        Assert.Equal([1.0, 2, 3, 4, 5, 6], arrays["c"].RealValues);
        Assert.Equal([1.0, 2, 3, 4, 5, 6], arrays["f"].RealValues);
        Assert.Equal([2, 3], arrays["f"].Axes.Select(a => a.Length));
        Assert.Equal([0.25], arrays["s"].RealValues);
        Assert.Equal([1.5, -2.0], arrays["h"].RealValues);
    }

    [Fact]
    public void APlainArrayOfAnotherType_IsRefusedNamingIt()
    {
        using var s = new MemoryStream();
        CmodelFile.Write(ComposedTestModels.Model(), s);
        using (var zip = new ZipArchive(s, ZipArchiveMode.Update, leaveOpen: true))
            Add(zip, "i", Npy("<i8", false, "(1,)", BitConverter.GetBytes(7L)));
        s.Position = 0;

        var ex = Assert.Throws<CmodelFormatException>(() => CmodelFile.Read(s));
        Assert.Equal("arrays/i.npy", ex.Field);
        Assert.Contains("'<i8'", ex.Message);
    }

    /// <summary>A table whose values carry no axis names takes each axis's sites from <c>&lt;name&gt;.&lt;input&gt;</c>;
    /// a list of the wrong length, or none at all, is refused by name.</summary>
    [Fact]
    public void APlainTable_TakesItsSitesFromItsInputLists()
    {
        double[] vgs = [-2, -1, 0, 1], vds = [0, 1, 2, 3, 4];
        var grid = Enumerable.Range(0, 20).Select(i => 0.01 * i).ToArray();
        Axis[] plainAxes = [new("axis1", [0, 1, 2, 3]), new("axis2", [0, 1, 2, 3, 4])];
        var arrays = new Dictionary<string, DataCube>
        {
            ["Id.values"]       = new(plainAxes, grid),
            ["Id.coefficients"] = new(plainAxes, grid),
            ["Id.vgs"]          = new([new Axis("axis1", [0, 1, 2, 3])], vgs),
            ["Id.vds"]          = new([new Axis("axis1", [0, 1, 2, 3, 4])], vds),
        };
        var domain = new TrainingDomain([(-2, 0), (1, 0), (1, 4), (-2, 4)], []);
        var model = ComposedTestModels.Model() with
        {
            Core = ComposedTestModels.Model().Core with { Id = Constituent.Table("Id", ["vgs", "vds"], domain) },
            Extrapolation = new ModelExtrapolation(new Dictionary<string, double> { ["Id"] = 1e-3 }),
            Arrays = arrays,
        };

        CmodelFile.Validate(model);

        var shortList = new Dictionary<string, DataCube>(arrays) { ["Id.vds"] = new([new Axis("axis1", [0, 1, 2, 3])], vds[..4]) };
        var ex = Assert.Throws<CmodelFormatException>(() => CmodelFile.Validate(model with { Arrays = shortList }));
        Assert.Equal("Core.Constituents.Id.Table", ex.Field);
        Assert.Contains("'Id.vds'", ex.Message);

        var noLists = new Dictionary<string, DataCube>(arrays);
        noLists.Remove("Id.vgs");
        ex = Assert.Throws<CmodelFormatException>(() => CmodelFile.Validate(model with { Arrays = noLists }));
        Assert.Contains("'Id.vgs'", ex.Message);
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var e = zip.CreateEntry($"{CmodelFile.ArraysFolder}{name}.npy").Open();
        e.Write(bytes);
    }

    private static byte[] F8(params double[] v) => [.. v.SelectMany(BitConverter.GetBytes)];

    /// <summary>A version-1 plain <c>.npy</c>, as <c>np.save</c> writes one.</summary>
    private static byte[] Npy(string dtype, bool fortran, string shape, byte[] data)
    {
        string header = $"{{'descr': '{dtype}', 'fortran_order': {(fortran ? "True" : "False")}, 'shape': {shape}, }}";
        header = header.PadRight(64 - 10 - 1) + "\n";
        return [0x93, .. "NUMPY"u8, 1, 0, .. BitConverter.GetBytes((ushort)header.Length), .. Encoding.ASCII.GetBytes(header), .. data];
    }
}
