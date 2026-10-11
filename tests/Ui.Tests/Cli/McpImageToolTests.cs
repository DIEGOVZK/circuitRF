// brief-img-11-cli-and-mcp.md §4, R-im11-5 — over the protocol, a picture's reading comes back as a picture: the
// `recognize` tool's result for a picture carries the JSON document and ONE image content item, the overlay the verb
// drew, with no argument asking for it (the default for a picture source) — and none when the caller says overlay=false.

using System.Diagnostics;
using System.Text.Json.Nodes;
using CircuitRF.Ui.Tests.Imaging;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class McpImageToolTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-im11-mcp-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void TheRecognizeTool_ReturnsThePicturesOverlay_AsOneImageBesideTheJson()
    {
        Directory.CreateDirectory(_root);
        string picture = Path.Combine(_root, "ladder.png");
        File.WriteAllBytes(picture, Pictures.Png(SchematicImageRecognitionTests.Ladder()));

        var calls = Serve(
            new JsonObject { ["name"] = "recognize", ["arguments"] = new JsonObject { ["path"] = picture } },
            new JsonObject { ["name"] = "recognize", ["arguments"] = new JsonObject { ["path"] = picture, ["overlay"] = false } });

        var content = calls[0]["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        var doc = JsonNode.Parse(content[0]!["text"]!.GetValue<string>())!;
        Assert.Equal("schematic", doc["result"]!["recognize"]!["image"]!["kind"]!.GetValue<string>());
        var image = Assert.Single(content, c => c!["type"]!.GetValue<string>() == "image")!;
        Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());
        byte[] png = Convert.FromBase64String(image["data"]!.GetValue<string>());
        Assert.Equal(SchematicImageRecognitionTests.Ladder().Width, CircuitRF.Design.Imaging.RasterImage.Decode(png).Image!.Width);

        Assert.Single(calls[1]["content"]!.AsArray());   // asked not to: the document alone
        Assert.Equal(["ladder.png"], Directory.GetFiles(_root).Select(Path.GetFileName));   // the scratch overlay is not kept
    }

    /// <summary>One <c>serve</c> process: initialize, then each tool call in turn; their results.</summary>
    private List<JsonObject> Serve(params JsonObject[] toolCalls)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in new[] { CliDll(), "serve", "--root", _root }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();

        JsonObject Ask(int id, string method, JsonObject parameters)
        {
            proc.StandardInput.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString());
            proc.StandardInput.Flush();
            while (proc.StandardOutput.ReadLine() is { } line)
                if (JsonNode.Parse(line) is JsonObject frame && frame["id"]?.GetValue<int>() == id) return frame["result"]!.AsObject();
            throw new InvalidOperationException("serve ended before answering");
        }

        Ask(1, "initialize", new JsonObject());
        var results = toolCalls.Select((call, k) => Ask(k + 2, "tools/call", call)).ToList();
        proc.StandardInput.Close();
        proc.WaitForExit(30_000);
        output.WriteLine(stderr.GetAwaiter().GetResult());
        return results;
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(McpImageToolTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }
}
