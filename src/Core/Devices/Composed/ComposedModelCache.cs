using System.Collections.Concurrent;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// One <c>.cmodel</c>, read, validated and compiled: what every <c>ComposedFET</c> on it shares.
/// </summary>
public sealed class LoadedComposedModel
{
    public string                  Path     { get; }
    public ComposedModel           Model    { get; }
    public ComposedNetworkTemplate Network  { get; }
    public ComposedCoreProgram     Program  { get; }

    /// <summary>The base finger count <c>N0</c>.</summary>
    public int N0 => Model.BasePeriphery.Fingers;

    internal LoadedComposedModel(string path, ComposedModel model)
    {
        Path    = path;
        Model   = model;
        Network = ComposedNetworkTemplate.Build(model.Network);
        Program = ComposedCoreProgram.Compile(model, System.IO.Path.GetFileName(path));
    }
}

/// <summary>
/// Process-wide cache of loaded <c>.cmodel</c> files, keyed by full path and re-read when the file's
/// size or last-write time changes — <c>TouchstoneCache</c>'s pattern and for its reason: a
/// parametric sweep re-elaborates at every point, and a design with a hundred instances of one
/// model should read it once (R-mc1-4). <see cref="LoadCountFor"/> is the counter that says so.
/// </summary>
public static class ComposedModelCache
{
    private sealed record Entry(long Ticks, long Length, Lazy<LoadedComposedModel> Model);

    private static readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> _loads = new(StringComparer.Ordinal);

    /// <summary>How many times the file at <paramref name="path"/> has actually been read (cache
    /// misses), process-wide. Per file, so a count is not disturbed by unrelated models loading
    /// alongside it.</summary>
    public static long LoadCountFor(string path)
        => _loads.GetValueOrDefault(System.IO.Path.GetFullPath(path));

    /// <summary>
    /// The loaded model at <paramref name="path"/>. Throws <see cref="CmodelFormatException"/> for a
    /// file the validator refuses, and the I/O exception for one that cannot be opened; the caller
    /// names the instance.
    /// </summary>
    public static LoadedComposedModel Load(string path)
    {
        string key = System.IO.Path.GetFullPath(path);
        var fi = new FileInfo(key);
        long ticks = fi.LastWriteTimeUtc.Ticks, length = fi.Length;

        while (true)
        {
            var entry = _cache.GetOrAdd(key, k => Make(k, ticks, length));
            if (entry.Ticks == ticks && entry.Length == length)
            {
                try { return entry.Model.Value; }
                catch { _cache.TryRemove(new(key, entry)); throw; }
            }

            var fresh = Make(key, ticks, length);
            if (_cache.TryUpdate(key, fresh, entry))
            {
                try { return fresh.Model.Value; }
                catch { _cache.TryRemove(new(key, fresh)); throw; }
            }
        }
    }

    private static Entry Make(string key, long ticks, long length)
        => new(ticks, length, new Lazy<LoadedComposedModel>(() =>
        {
            _loads.AddOrUpdate(key, 1, (_, n) => n + 1);
            return new LoadedComposedModel(key, CmodelFile.Read(key));
        }, LazyThreadSafetyMode.ExecutionAndPublication));
}
