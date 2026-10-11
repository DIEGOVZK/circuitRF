// brief-imf-9-auto-settings-and-paste.md R-imf9-6 — a cancelled read stops at its next checkpoint and returns cancelled:
// nothing thrown, nothing made, and no stage begun after the token was cancelled.

using System;
using System.Collections.Generic;
using System.Threading;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class ReadCancellationTests
{
    /// <summary>Reports each stage as it begins, on the reading's own thread — Progress&lt;T&gt; would post it.</summary>
    private sealed class Stages(Action<string> begun) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => begun(value.Stage);
    }

    public static TheoryData<string> Checkpoints() =>
    [
        "Is it a schematic", "Reading the line work", "Finding words", "Tracing wires", "Finding symbols", "Naming symbols",
        "Reading words", "Making the circuit",
    ];

    [Theory]
    [MemberData(nameof(Checkpoints))]
    public void CancelledAsAStageBegins_TheReadReturnsCancelled_AndBeginsNoOther(string stage)
    {
        using var cts = new CancellationTokenSource();
        string? cancelledAt = null;
        var after = new List<string>();
        var control = new RunControl
        {
            Token = cts.Token,
            Progress = new Stages(s =>
            {
                if (cancelledAt is not null) { if (s != cancelledAt) after.Add(s); }
                else if (s == stage) { cancelledAt = s; cts.Cancel(); }
            }),
        };

        var r = SchematicImageRecognition.Circuit(new SchematicImageInput { Source = ReadingPlausibilityTests.Picture("ds-ldo-2x") }, null, control);

        Assert.Equal(stage, cancelledAt);
        Assert.True(r.Cancelled);
        Assert.False(r.Ok);
        Assert.Equal(SchematicImageRecognition.CancelledRefusal, r.Refusal);
        Assert.Empty(after);   // the counter: stages begun after the token was cancelled
    }

    /// <summary>Thinning a large picture is the longest pass with no stage of its own: it checks once a pass.</summary>
    [Fact]
    public void Thinning_StopsAtItsNextPass()
    {
        var mask = new BinaryImage(300, 300);
        for (int y = 20; y < 280; y++)
            for (int x = 20; x < 280; x++) mask.Pixels[y * 300 + x] = 1;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Skeleton.Thin(mask, cts.Token));
    }
}
