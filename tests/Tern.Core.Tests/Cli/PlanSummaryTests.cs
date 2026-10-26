using System.Diagnostics;

using Tern.Cli.Commands;
using Tern.Cli.Common;
using Tern.Core.Planning;

namespace Tern.Core.Tests.Cli;

public class PlanSummaryTests
{
    private static PlanOperation Op(PlanAction action, string module) =>
        new() { Action = action, Module = module, Include = "i", Database = "master", Id = Guid.NewGuid(), Path = "/sitecore/content/x" };

    [Fact]
    public void Counts_name_only_the_actions_there_are() =>
        Assert.Equal("2 added, 1 deleted", SyncRunner.CountByAction([Op(PlanAction.Create, "a"), Op(PlanAction.Delete, "a"), Op(PlanAction.Create, "a")]));

    [Fact]
    public void No_module_table_for_one_module() => Assert.Empty(SyncRunner.ModuleTable([Op(PlanAction.Create, "a"), Op(PlanAction.Update, "a")]));

    [Fact]
    public void Module_table_lists_each_module_with_its_counts()
    {
        var rows = SyncRunner.ModuleTable([Op(PlanAction.Create, "Project.Site"), Op(PlanAction.Update, "Feature.Nav"), Op(PlanAction.Update, "Feature.Nav")]);
        Assert.Equal(
        [
            "Changes by module:",
            "  Module        added  updated",
            "  Feature.Nav       -        2",
            "  Project.Site      1        -",
            "",
        ], rows);
    }

    [Fact]
    public void Plan_lines_end_with_counts_and_the_module_table()
    {
        using var err = new StringWriter();
        var output = new Output(json: false, verbose: false, trace: false) { Err = err, Colors = false };
        var include = new Tern.Core.Configuration.Include { Name = "x", Path = Tern.Core.Configuration.ItemPath.Parse("/sitecore/content"), Database = "master" };
        var ops = new List<PlanOperation> { Op(PlanAction.Create, "a"), Op(PlanAction.Update, "b") };
        var plan = new Plan { Direction = PlanDirection.Push, Source = "files", Target = "dev" };
        plan.Operations.AddRange(ops);
        SyncRunner.PrintPlan(output, new PlanResult(plan, [new IncludePlan(include, ops, 10)], 10), "dev", page: true);
        var text = err.ToString();
        Assert.Contains("[master] Discovered 2 changes after evaluating 10 total items." + Environment.NewLine + "[master] 1 added, 1 updated" + Environment.NewLine, text);
        Assert.Contains("Changes by module:", text);
    }

    [Fact]
    public void Quiet_leaves_out_each_operation_but_keeps_the_summary()
    {
        using var err = new StringWriter();
        var output = new Output(json: false, verbose: false, trace: false) { Err = err, Colors = false, Quiet = true };
        var include = new Tern.Core.Configuration.Include { Name = "x", Path = Tern.Core.Configuration.ItemPath.Parse("/sitecore/content"), Database = "master" };
        var ops = new List<PlanOperation> { Op(PlanAction.Create, "a"), Op(PlanAction.Update, "b") };
        var plan = new Plan { Direction = PlanDirection.Push, Source = "files", Target = "dev" };
        plan.Operations.AddRange(ops);
        SyncRunner.PrintPlan(output, new PlanResult(plan, [new IncludePlan(include, ops, 10)], 10), "dev");
        var text = err.ToString();
        Assert.DoesNotContain("/sitecore/content/x", text);
        Assert.Contains("[master] Discovered 2 changes after evaluating 10 total items." + Environment.NewLine + "[master] 1 added, 1 updated" + Environment.NewLine, text);
        Assert.Contains("Changes by module:", text);
    }

    [Fact]
    public void Phases_leave_out_steps_that_took_no_time()
    {
        var phases = new Phases(Stopwatch.StartNew());
        phases.End("read");
        Assert.DoesNotContain("(", phases.Summary());
    }

    [Theory]
    [InlineData("less -R", "less", "-R")]
    [InlineData("\"C:/Program Files/Git/usr/bin/less.exe\" -FRX", "C:/Program Files/Git/usr/bin/less.exe", "-FRX")]
    [InlineData("more", "more", "")]
    public void Splits_a_pager_command(string command, string file, string arguments) => Assert.Equal((file, arguments), Pager.Split(command));

    [Theory]
    [InlineData("C:/Git/usr/bin/less.exe", "", "\"" + Pager.Prompt + "\"")]
    [InlineData("less", "-R", "-R \"" + Pager.Prompt + "\"")]
    [InlineData("less", "-R -Psmine", "-R -Psmine")]
    [InlineData("more", "", "")]
    public void Gives_less_a_prompt_with_the_keys(string file, string arguments, string expected) => Assert.Equal(expected, Pager.WithPrompt(file, arguments));
}
