using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using CircuitRF.Cli;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Ui.Tests.Lvs;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>brief-modelcomposer-1 R-mc1-2: <c>check</c> classifies a <c>.cmodel</c> and reports the
/// model validator's own sentence, with no rule of its own.</summary>
[Collection(LvsCliConsoleCollection.Name)]
public sealed class CheckCmodelTests
{
    [Fact]
    public void ABrokenModel_ReportsTheValidatorsSentence_AndExitsOne()
    {
        string good = ComposedTestModels.WriteTemp(ComposedTestModels.Model());
        string bad  = Path.Combine(Path.GetDirectoryName(good)!, "bad.cmodel");
        string json = Encoding.UTF8.GetString(CmodelFile.ModelJson(ComposedTestModels.Model()))
                          .Replace("\"Method\": \"Equation\"", "\"Method\": \"Table\"");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create))
        using (var e = zip.CreateEntry(CmodelFile.ModelEntry).Open())
            e.Write(Encoding.UTF8.GetBytes(json));

        string expected = Assert.Throws<CmodelFormatException>(() => CmodelFile.Read(bad)).Message;

        var (exitBad, outBad) = Check(bad);
        Assert.Equal(1, exitBad);
        Assert.Contains(expected, outBad);

        var (exitGood, _) = Check(good);
        Assert.Equal(0, exitGood);
    }

    private static (int Exit, string Out) Check(string path)
    {
        // Findings go to stderr and the tally to stdout; both are read.
        var (realOut, realErr) = (Console.Out, Console.Error);
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            Console.SetError(buffer);
            JsonRun.Reset();
            return (CliEntry.Run(["check", path]), buffer.ToString());
        }
        finally { Console.SetOut(realOut); Console.SetError(realErr); }
    }
}
