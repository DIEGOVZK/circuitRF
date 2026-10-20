using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RfCore.Data;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// A <c>.cmodel</c> array written as a PLAIN NumPy <c>.npy</c> — one array, as <c>np.save</c> or a MATLAB <c>.npy</c>
/// writer produces it — rather than circuitRF's own structured layout with its <c>__meta__</c> field.
///
/// <para><b>Why it is read.</b> A model written by a script of its own has no reason to know circuitRF's result layout,
/// and building that layout by hand (a structured dtype, a JSON blob, row-major order) was the hardest step of writing one.
/// A plain array carries everything an ANN needs; it carries no axis names or sites, so a table's sites then come from
/// <c>&lt;name&gt;.&lt;input&gt;</c> arrays (<see cref="RuntimePayload"/>).</para>
///
/// <para><b>What it accepts:</b> <c>float64</c> or <c>float32</c>, little-endian, in either order (<c>fortran_order</c>
/// True is what a column-major writer emits, and is reordered here). Its axes are named <c>axis1</c>, <c>axis2</c>, … with
/// the values 0, 1, 2, …; a 0-d array is a list of one.</para>
/// </summary>
internal static class PlainNpy
{
    private static readonly byte[] Magic = [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'];

    /// <summary>The array in <paramref name="bytes"/>, or null when it is not a plain <c>.npy</c> — a structured one, whose
    /// <c>descr</c> is a list, or not a <c>.npy</c> at all — which the caller then reads as circuitRF's own.</summary>
    public static DataCube? TryRead(byte[] bytes, string entry)
    {
        if (bytes.Length < 10 || !bytes.AsSpan(0, 6).SequenceEqual(Magic)) return null;
        int major = bytes[6];
        int headerStart = major == 1 ? 10 : 12;
        if (major is not (1 or 2 or 3) || bytes.Length < headerStart) return null;
        int headerLength = major == 1 ? BitConverter.ToUInt16(bytes, 8) : (int)BitConverter.ToUInt32(bytes, 8);
        if (headerStart + headerLength > bytes.Length) return null;
        string header = Encoding.Latin1.GetString(bytes, headerStart, headerLength);

        var descr = Regex.Match(header, @"['""]descr['""]\s*:\s*(\S)");
        if (!descr.Success || descr.Groups[1].Value is not ("'" or "\"")) return null;
        string dtype = Regex.Match(header, @"['""]descr['""]\s*:\s*['""]([^'""]*)['""]").Groups[1].Value;
        int size = dtype switch
        {
            "<f8" => 8,
            "<f4" => 4,
            _ => throw new CmodelFormatException(entry,
                     $"a plain array holds float64 or float32 ('<f8' or '<f4'); this one is '{dtype}'."),
        };
        bool fortran = Regex.IsMatch(header, @"['""]fortran_order['""]\s*:\s*True");
        var shapeMatch = Regex.Match(header, @"['""]shape['""]\s*:\s*\(([^)]*)\)");
        if (!shapeMatch.Success) throw new CmodelFormatException(entry, "the .npy header has no shape.");
        int[] shape = [.. shapeMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                    .Select(s => int.Parse(s, CultureInfo.InvariantCulture))];

        int count = shape.Aggregate(1, (a, b) => checked(a * b));
        int start = headerStart + headerLength;
        if (bytes.Length - start < (long)count * size)
            throw new CmodelFormatException(entry, $"the array is shorter than its shape ({string.Join(" × ", shape)}).");

        var stored = new double[count];
        for (int i = 0; i < count; i++)
            stored[i] = size == 8 ? BitConverter.ToDouble(bytes, start + 8 * i) : BitConverter.ToSingle(bytes, start + 4 * i);

        if (shape.Length == 0) shape = [1];
        double[] data = fortran && shape.Length > 1 ? ToRowMajor(stored, shape) : stored;
        var axes = new Axis[shape.Length];
        for (int k = 0; k < shape.Length; k++)
            axes[k] = new Axis($"axis{k + 1}", [.. Enumerable.Range(0, shape[k]).Select(i => (double)i)]);
        return new DataCube(axes, data);
    }

    /// <summary>Column-major (first index fastest) to row-major (last index fastest).</summary>
    private static double[] ToRowMajor(double[] columnMajor, int[] shape)
    {
        var result = new double[columnMajor.Length];
        var index = new int[shape.Length];
        for (int r = 0; r < result.Length; r++)
        {
            // r is the row-major position of `index`; find its column-major position.
            int c = 0;
            for (int k = shape.Length - 1; k >= 0; k--) c = c * shape[k] + index[k];
            result[r] = columnMajor[c];
            for (int k = shape.Length - 1; k >= 0; k--)
            {
                if (++index[k] < shape[k]) break;
                index[k] = 0;
            }
        }
        return result;
    }
}
