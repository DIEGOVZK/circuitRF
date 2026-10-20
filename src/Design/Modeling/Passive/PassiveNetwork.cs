// A passive N-port as a schematic (brief-modelcomposer-12 R-mc12-3, R-mc12-4): pins 1…N are its ports and every port
// is referenced to the schematic's ground, so a stock topology, a user-drawn one and a pasted one are read and solved
// the same way — through PinNetwork, by the engine, with nothing stamped here.

using System.Numerics;
using CircuitRF.Engine;

namespace CircuitRF.Design.Modeling.Passive;

/// <summary>An N-pin schematic whose pin k is port k, referenced to ground.</summary>
public sealed class PassiveNetwork : PinNetwork
{
    private const string Role = "a passive topology";

    private PassiveNetwork(Loaded l) : base(l) { }

    /// <summary>The port count — the schematic's pin count.</summary>
    public int Ports => PinNets.Count;

    /// <summary>Reads a topology from a file: pins numbered 1…N, each once (<paramref name="ports"/> 0 takes however many
    /// there are; otherwise exactly that many).</summary>
    public static PassiveNetwork Load(string cschPath, int ports = 0) => new(Read(cschPath, ports, Role));

    /// <summary>Reads a topology held as text — a library resource; <paramref name="name"/> stands in for its path.</summary>
    public static PassiveNetwork FromText(string name, string json, int ports = 0) => new(ReadText(name, json, ports, Role));

    /// <summary>
    /// S referenced to <paramref name="z0"/> on every port at every frequency, the variables at <paramref name="values"/>
    /// (null keeps the file's own). <c>S[f][i, j]</c>.
    /// </summary>
    public Complex[][,] S(double[] freqsHz, double z0, IReadOnlyDictionary<string, double>? values = null, RunControl? rc = null) =>
        Scattering(freqsHz, values, _ => null, [.. PinNets.Select(n => (n, "0"))], z0, rc);
}
