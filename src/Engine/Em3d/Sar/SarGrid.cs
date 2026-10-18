// brief-sar-5 R-sar5-1 — the SAR engine's input: a Cartesian grid of cells, each with the power it dissipates per unit
// volume, its density and its volume. Solver-neutral (overview L5): nothing here knows which solver wrote the field or
// what file it came from; src/Design builds a grid from openEMS's raw SAR dump, a later Palace series from a resampled
// field.
//
// E IS REDUCED AS IT ARRIVES. The input per cell is the peak complex phasor (Ex, Ey, Ez), the conductivity σ, the
// density ρ and the volume V. The engine's only use for E and σ is the dissipated power density ½σ|E|² (W/m³), so
// SarGridBuilder computes that per cell as each slab streams in and keeps it instead: 4 bytes a cell rather than the
// 28 that E and σ take as float32, which is what lets a whole head at 1 mm (~10 M cells) fit on a 16 GB machine
// (R-sar5-5). A cell with ρ = 0 is background (air): its power density is kept as zero, exactly as openEMS's sar_calc
// zeroes it, and whatever σ|E|² it held is reported separately (UncountedPowerW) so a lossy material that was given no
// density shows up rather than vanishing.
//
// Array order: C order over (x, y, z), z fastest — cell (i, j, k) is at (i·ny + j)·nz + k.

using System.Numerics;
using System.Runtime.InteropServices;

namespace CircuitRF.Engine.Em3d.Sar;

/// <summary>
/// One axis of a <see cref="SarGrid"/>: its cell widths in metres and the coordinate of its first cell's lower face.
/// The cell faces are cumulative from there.
/// </summary>
public sealed class SarAxis
{
    /// <summary>The cell widths, metres, each &gt; 0.</summary>
    public double[] Widths { get; }

    /// <summary>The cell faces, metres: <c>Widths.Length + 1</c> values, strictly increasing.</summary>
    public double[] Faces { get; }

    public int Count => Widths.Length;

    public SarAxis(double firstFaceM, double[] widths)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (widths.Length == 0) throw new ArgumentException("An axis has at least one cell.", nameof(widths));
        Widths = widths;
        Faces = new double[widths.Length + 1];
        Faces[0] = firstFaceM;
        for (int i = 0; i < widths.Length; i++)
        {
            if (!(widths[i] > 0) || !double.IsFinite(widths[i]))
                throw new ArgumentException($"Cell {i} has width {widths[i]}; every cell width is positive.", nameof(widths));
            Faces[i + 1] = Faces[i] + widths[i];
        }
    }

    /// <summary>An axis given by its cell centres and widths, as openEMS's raw SAR dump states it.</summary>
    public static SarAxis FromCentres(double[] centres, double[] widths)
    {
        ArgumentNullException.ThrowIfNull(centres);
        ArgumentNullException.ThrowIfNull(widths);
        if (centres.Length != widths.Length)
            throw new ArgumentException($"{centres.Length} cell centres and {widths.Length} widths.", nameof(widths));
        if (centres.Length == 0) throw new ArgumentException("An axis has at least one cell.", nameof(centres));
        return new SarAxis(centres[0] - widths[0] / 2, widths);
    }

    /// <summary>The centre of cell <paramref name="i"/>, metres.</summary>
    public double Centre(int i) => Faces[i] + Widths[i] / 2;
}

/// <summary>
/// The SAR engine's input (R-sar5-1): three axes and, per cell, the dissipated power density ½σ|E|² (W/m³, zero in
/// background), the density ρ (kg/m³, zero for background) and the volume (m³). Built by <see cref="SarGridBuilder"/>.
/// </summary>
public sealed class SarGrid
{
    public SarAxis X { get; }
    public SarAxis Y { get; }
    public SarAxis Z { get; }

    /// <summary>½σ|E|² per cell, W/m³; zero where <see cref="Density"/> is zero.</summary>
    public float[] PowerDensity { get; }

    /// <summary>ρ per cell, kg/m³; zero is background.</summary>
    public float[] Density { get; }

    /// <summary>The cell volumes, m³.</summary>
    public float[] Volume { get; }

    /// <summary>Σ ½σ|E|²V over the background cells (ρ = 0), W: power the field dissipated in cells that state no density,
    /// which no SAR counts. Non-zero means a lossy material was given no density.</summary>
    public double UncountedPowerW { get; }

    public int Nx => X.Count;
    public int Ny => Y.Count;
    public int Nz => Z.Count;
    public int Count => Nx * Ny * Nz;

    internal SarGrid(SarAxis x, SarAxis y, SarAxis z, float[] powerDensity, float[] density, float[] volume, double uncountedPowerW)
    {
        X = x; Y = y; Z = z;
        PowerDensity = powerDensity; Density = density; Volume = volume;
        UncountedPowerW = uncountedPowerW;
    }

    /// <summary>The flat index of cell (i, j, k).</summary>
    public int Index(int i, int j, int k) => (i * Ny + j) * Nz + k;

    /// <summary>Cell (i, j, k) of flat index <paramref name="index"/>.</summary>
    public (int I, int J, int K) Cell(int index)
    {
        int k = index % Nz;
        int ij = index / Nz;
        return (ij / Ny, ij % Ny, k);
    }

    /// <summary>A grid from whole arrays at once: complex E per component, σ, ρ and V per cell (C order over (x, y, z)).</summary>
    public static SarGrid FromCells(SarAxis x, SarAxis y, SarAxis z, Complex[] ex, Complex[] ey, Complex[] ez,
                                    float[] conductivity, float[] density, float[] volume)
    {
        var b = new SarGridBuilder(x, y, z);
        b.AddSlab(0, x.Count, ex, ey, ez, conductivity, density, volume);
        return b.Build();
    }
}

/// <summary>
/// Builds a <see cref="SarGrid"/> one x slab at a time, so a dump larger than memory is never held whole (R-sar5-5).
/// Each slab's E is reduced to ½σ|E|² on arrival (see the file header). Every x index must be added exactly once.
/// </summary>
public sealed class SarGridBuilder
{
    private readonly SarAxis _x, _y, _z;
    private readonly float[] _power, _density, _volume;
    private readonly bool[] _added;
    private double _uncounted;

    public SarGridBuilder(SarAxis x, SarAxis y, SarAxis z)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(z);
        _x = x; _y = y; _z = z;
        long n = (long)x.Count * y.Count * z.Count;
        if (n > Array.MaxLength) throw new ArgumentException($"{n} cells is more than one array can hold.");
        _power = new float[n];
        _density = new float[n];
        _volume = new float[n];
        _added = new bool[x.Count];
    }

    /// <summary>
    /// Adds x indices [<paramref name="xStart"/>, xStart + <paramref name="xCount"/>). Each E component is the cells'
    /// complex64 phasors as interleaved (re, im) float pairs — the layout a solver's single-precision dump has, taken
    /// without a copy.
    /// </summary>
    public void AddSlab(int xStart, int xCount, ReadOnlySpan<float> exReIm, ReadOnlySpan<float> eyReIm, ReadOnlySpan<float> ezReIm,
                        ReadOnlySpan<float> conductivity, ReadOnlySpan<float> density, ReadOnlySpan<float> volume)
    {
        int plane = _y.Count * _z.Count;
        int cells = CheckSlab(xStart, xCount, plane);
        if (exReIm.Length != 2 * cells || eyReIm.Length != 2 * cells || ezReIm.Length != 2 * cells)
            throw new ArgumentException($"A slab of {cells} cells needs {2 * cells} floats per E component (re, im pairs).");
        CheckScalars(cells, conductivity, density, volume);
        int offset = xStart * plane;
        double uncounted = 0;
        for (int c = 0; c < cells; c++)
        {
            double e2 = Sq(exReIm[2 * c]) + Sq(exReIm[2 * c + 1]) + Sq(eyReIm[2 * c]) + Sq(eyReIm[2 * c + 1])
                      + Sq(ezReIm[2 * c]) + Sq(ezReIm[2 * c + 1]);
            uncounted += Store(offset + c, 0.5 * conductivity[c] * e2, density[c], volume[c]);
        }
        Commit(xStart, xCount, uncounted);
    }

    /// <summary><see cref="AddSlab(int,int,ReadOnlySpan{float},ReadOnlySpan{float},ReadOnlySpan{float},ReadOnlySpan{float},ReadOnlySpan{float},ReadOnlySpan{float})"/>
    /// with double-precision complex E.</summary>
    public void AddSlab(int xStart, int xCount, ReadOnlySpan<Complex> ex, ReadOnlySpan<Complex> ey, ReadOnlySpan<Complex> ez,
                        ReadOnlySpan<float> conductivity, ReadOnlySpan<float> density, ReadOnlySpan<float> volume)
    {
        int plane = _y.Count * _z.Count;
        int cells = CheckSlab(xStart, xCount, plane);
        if (ex.Length != cells || ey.Length != cells || ez.Length != cells)
            throw new ArgumentException($"A slab of {cells} cells needs {cells} values per E component.");
        CheckScalars(cells, conductivity, density, volume);
        int offset = xStart * plane;
        double uncounted = 0;
        for (int c = 0; c < cells; c++)
        {
            double e2 = MagSq(ex[c]) + MagSq(ey[c]) + MagSq(ez[c]);
            uncounted += Store(offset + c, 0.5 * conductivity[c] * e2, density[c], volume[c]);
        }
        Commit(xStart, xCount, uncounted);
    }

    /// <summary>The grid. Refused while any x index is missing.</summary>
    public SarGrid Build()
    {
        int missing = Array.IndexOf(_added, false);
        if (missing >= 0) throw new InvalidOperationException($"x index {missing} was never added.");
        return new SarGrid(_x, _y, _z, _power, _density, _volume, _uncounted);
    }

    private double Store(int at, double powerDensity, float density, float volume)
    {
        if (!(density >= 0) || !(volume >= 0) || !double.IsFinite(powerDensity) || !(powerDensity >= 0))
            throw new ArgumentException($"Cell {at} has density {density}, volume {volume} and power density {powerDensity}; " +
                                        "each must be finite and non-negative.");
        _density[at] = density;
        _volume[at] = volume;
        if (density > 0)
        {
            _power[at] = (float)powerDensity;
            return 0;
        }
        return powerDensity * volume;
    }

    private int CheckSlab(int xStart, int xCount, int plane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(xStart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(xCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(xStart + xCount, _x.Count);
        for (int i = xStart; i < xStart + xCount; i++)
            if (_added[i]) throw new ArgumentException($"x index {i} was already added.");
        return xCount * plane;
    }

    private static void CheckScalars(int cells, ReadOnlySpan<float> conductivity, ReadOnlySpan<float> density, ReadOnlySpan<float> volume)
    {
        if (conductivity.Length != cells || density.Length != cells || volume.Length != cells)
            throw new ArgumentException($"A slab of {cells} cells needs {cells} conductivities, densities and volumes.");
    }

    private void Commit(int xStart, int xCount, double uncounted)
    {
        for (int i = xStart; i < xStart + xCount; i++) _added[i] = true;
        _uncounted += uncounted;
    }

    private static double Sq(float v) => (double)v * v;
    private static double MagSq(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
}

/// <summary>Reinterprets a solver's complex64 values as interleaved (re, im) floats, without copying.</summary>
public static class SarComplex64
{
    /// <summary>The (re, im) floats of <paramref name="values"/>, which must be a struct of exactly two floats.</summary>
    public static ReadOnlySpan<float> AsFloats<T>(ReadOnlySpan<T> values) where T : struct
    {
        if (Marshal.SizeOf<T>() != 2 * sizeof(float))
            throw new ArgumentException($"{typeof(T).Name} is not a pair of floats.");
        return MemoryMarshal.Cast<T, float>(values);
    }
}
