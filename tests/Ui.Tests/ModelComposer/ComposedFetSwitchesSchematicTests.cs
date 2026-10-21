using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Render;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-21 R-mc21-6, R-mc21-7: a composed FET's switches in a schematic — written to the netlist only when
/// not the default, so an untouched schematic extracts exactly as before, and drawn under a switched instance.
/// </summary>
public sealed class ComposedFetSwitchesSchematicTests
{
    private static EditableComponent Fet(params (string Name, string Expression)[] switches)
    {
        var c = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.ComposedFET, X = 0, Y = 0 };
        c.Parameters.Add(new EditableParameter { Name = "File", Expression = "gan.cmodel" });
        foreach (var (name, expression) in switches)
            c.Parameters.Add(new EditableParameter { Name = name, Expression = expression, ShowOnSchematic = false });
        return c;
    }

    private static SchematicEditModel Model(EditableComponent c)
    {
        var m = new SchematicEditModel();
        m.Components.Add(c);
        return m;
    }

    private static string Cnl(EditableComponent c) => CnlWriter.Write(NetExtractor.Extract(Model(c)).TestBench);

    [Fact]
    public void DefaultSwitches_ExtractByteIdentically_AndANonDefaultOneIsWritten()
    {
        var defaults = ComponentTypeRegistry.DefaultParameters(SymbolKind.ComposedFET, 3)
            .Where(d => d.Name is not ("File" or "N" or "Symbol" or "Rth" or "Cth" or "Temp")).Select(d => (d.Name, d.Expression)).ToArray();
        Assert.Equal(10, defaults.Length);

        string before = Cnl(Fet());
        Assert.Equal(before, Cnl(Fet(defaults)));
        Assert.Equal(before, Cnl(Fet(("GateLag", "on"), ("LinearC", "0"))));

        string switched = Cnl(Fet(("LinearC", "true"), ("GateLag", "gl")));
        Assert.Contains("LinearC=true", switched);
        Assert.Contains("GateLag=gl", switched);
    }

    [Fact]
    public void TheEditor_GroupsTheSwitches_AsTrueFalsePickers_AndAnExpressionAsText()
    {
        var comp = Fet(("GateLag", "gl"));   // placed before the other nine existed: the editor tops them up
        var model = Model(comp);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        Assert.True(editor.ShowModelSwitches);
        Assert.Equal(["GateLag", "Charge", "LinearC", "LinearCgs", "LinearCdg", "LinearCds", "LinearCm", "DrainLag", "SelfHeating",
                      "Dispersion"], editor.SwitchRows.Select(r => r.Name));
        Assert.DoesNotContain(editor.Rows, r => r.Name is "Charge" or "LinearC");
        foreach (var row in editor.SwitchRows.Where(r => r.Name != "GateLag"))
        {
            Assert.True(row.IsChoiceParam, row.Name);
            Assert.Equal(["true", "false"], row.ChoiceOptions);
        }
        var gl = editor.SwitchRows.Single(r => r.Name == "GateLag");
        Assert.False(gl.IsChoiceParam);
        Assert.True(gl.ShowExpressionTextBox);
    }

    [Fact]
    public void ASwitchedInstance_IsAnnotated_AndADefaultOneIsNot()
    {
        static IEnumerable<string> Texts(EditableComponent c)
        {
            var (rm, idx) = Model(c).BuildRenderModel();
            var theme = SchematicRenderTheme.FromTheme(ThemeResolver.Resolve(ThemeResolver.DefaultThemeName), ColorVariant.Light);
            return SchematicRenderer.MeasureTexts((1600, 1200), rm, idx, -800, -600, 1.0, theme).Select(t => t.Text);
        }

        Assert.Contains("linear C · no gate lag", Texts(Fet(("LinearC", "true"), ("GateLag", "off"), ("Charge", "true"))));
        Assert.DoesNotContain(Texts(Fet(("LinearC", "false"), ("GateLag", "true"))), t => t.Contains("linear C") || t.Contains("lag"));
    }
}
