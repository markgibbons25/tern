using Tern.Cli.Commands;
using Tern.Cli.Common;
using Tern.Core.Planning;

namespace Tern.Core.Tests.Cli;

public class OperationColorTests
{
    [Theory]
    [InlineData(PlanAction.Create, "32")]
    [InlineData(PlanAction.Update, "33")]
    [InlineData(PlanAction.Delete, "31")]
    [InlineData(PlanAction.Move, "36")]
    [InlineData(PlanAction.Rename, "36")]
    public void Each_action_has_its_colour(PlanAction action, string color) => Assert.Equal(color, SyncRunner.ColorOf(action));

    [Fact]
    public void Colours_a_line_on_a_terminal()
    {
        using var err = new StringWriter();
        using var @out = new StringWriter();
        var output = new Output(json: false, verbose: false, trace: false) { Err = err, Out = @out, Colors = true };
        output.Info("[master] [A] /sitecore/content/Home", "32");
        output.Line("[master] [D] /sitecore/content/Old", "31");
        Assert.Equal("\u001b[32m[master] [A] /sitecore/content/Home\u001b[0m" + Environment.NewLine, err.ToString());
        Assert.Equal("\u001b[31m[master] [D] /sitecore/content/Old\u001b[0m" + Environment.NewLine, @out.ToString());
    }

    [Fact]
    public void Leaves_a_line_plain_when_colours_are_off()
    {
        using var err = new StringWriter();
        var output = new Output(json: false, verbose: false, trace: false) { Err = err, Colors = false };
        output.Info("[master] [A] /sitecore/content/Home", "32");
        Assert.Equal("[master] [A] /sitecore/content/Home" + Environment.NewLine, err.ToString());
    }

    [Fact]
    public void Prints_no_line_under_json()
    {
        using var @out = new StringWriter();
        new Output(json: true, verbose: false, trace: false) { Out = @out, Colors = true }.Line("[master] [A] /x", "32");
        Assert.Empty(@out.ToString());
    }
}
