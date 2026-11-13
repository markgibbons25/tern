using System.CommandLine;

using Tern.Cli.Common;
using Tern.Core.Analysis;
using Tern.Core.Configuration;
using Tern.Core.Trees;
using Tern.Remote.Management;

namespace Tern.Cli.Commands;

/// <summary>
/// <c>ser analyze</c>: finds the Includes that make the CM list far more items than they
/// serialize. Management Services lists an Include's whole scope and the rules drop items only
/// afterwards, so a rule that drops a large branch still costs every pull and push the time to
/// read it (<see cref="ReadCost"/>).
/// </summary>
public static class AnalyzeCommand
{
    public static Command Build()
    {
        var environment = CommonOptions.EnvironmentName();
        var include = CommonOptions.Include();
        var exclude = CommonOptions.Exclude();
        var offline = new Option<bool>("--offline") { Description = "Check the configuration only, without reading from the CM. Lists every rule that makes the CM list items it then drops, without how many" };
        var cmd = new Command("analyze", "Find includes that read far more items from the CM than they serialize, which slows every pull and push") { environment, include, exclude, offline };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var modules = session.SelectModules(parse.GetValue(include)!, parse.GetValue(exclude)!);
            var includes = modules.SelectMany(m => m.Includes).ToList();
            if (parse.GetValue(offline))
            {
                return Offline(session.Output, includes);
            }
            var endpoint = session.Endpoint(parse.GetValue(environment));
            var cm = new CmItemTree(session.Serialization(endpoint), [.. session.ExcludedFields(modules)], endpoint.Name);
            await SyncRunner.ReadAsync(session.Output, includes, [(cm, endpoint.Name)], FileItemTree.CountFiles, token);
            return Live(session.Output, endpoint.Name, includes.Select(i => ReadCost.Measure(i, cm.Read(i) ?? [])).ToList());
        }, ct));
        return cmd;
    }

    private static int Live(Output output, string endpointName, List<IncludeReadCost> costs)
    {
        var over = costs.Where(c => c.OverReads).OrderByDescending(c => c.DroppedItems).ToList();
        output.Document("ser analyze", w =>
        {
            w.WriteBoolean("offline", false);
            w.WriteString("endpoint", endpointName);
            w.WriteNumber("read", costs.Sum(c => c.Read));
            w.WriteNumber("kept", costs.Sum(c => c.Kept));
            w.WriteNumber("overReading", over.Count);
            w.WriteNumber("minDropped", ReadCost.MinDropped);
            w.WriteStartArray("includes");
            foreach (var c in costs)
            {
                w.WriteStartObject();
                WriteInclude(w, c.Include);
                w.WriteNumber("read", c.Read);
                w.WriteNumber("kept", c.Kept);
                w.WriteNumber("dropped", c.DroppedItems);
                w.WriteBoolean("overReads", c.OverReads);
                w.WriteStartArray("rules");
                foreach (var d in c.Dropped)
                {
                    w.WriteStartObject();
                    WriteRule(w, d.Rule);
                    w.WriteNumber("dropped", d.Items);
                    w.WriteStartArray("places");
                    foreach (var p in d.Places)
                    {
                        w.WriteStartObject();
                        w.WriteString("below", p.Below);
                        w.WriteNumber("items", p.Items);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    if (d.Advice is { } advice)
                    {
                        w.WriteString("advice", advice);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

        foreach (var c in over)
        {
            output.Line($"[{c.Include.Module.Namespace}] {c.Include.Name} ({c.Include.Path}, {c.Include.Scope}): read {c.Read:N0} items, kept {c.Kept:N0}{Ratio(c.Read, c.Kept)}", Output.WriteColor);
            foreach (var d in c.Dropped)
            {
                var by = d.Rule is { } rule ? $"rule {rule.Path} ({rule.Scope})" : "the include's scope";
                output.Line($"  {d.Items:N0} dropped by {by}, below:");
                foreach (var p in d.Places.Take(3))
                {
                    output.Line($"    {p.Below} ({p.Items:N0})");
                }
                if (d.Places.Count > 3)
                {
                    output.Line($"    and {d.Places.Count - 3:N0} more places ({d.Places.Skip(3).Sum(p => p.Items):N0})");
                }
                if (d.Advice is { } advice)
                {
                    output.Line($"  {advice}", Output.Dim);
                }
            }
            output.Line();
        }
        output.Line($"Read {costs.Sum(c => c.Read):N0} items from {endpointName} for {Includes(costs.Count)}, and kept {costs.Sum(c => c.Kept):N0}.");
        if (over.Count == 0)
        {
            output.Line("No issues detected.", SyncRunner.SummaryColor);
            return ExitCodes.Success;
        }
        output.Line($"{Includes(over.Count)} read {over.Sum(c => c.Read):N0} items to keep {over.Sum(c => c.Kept):N0}: the rest is read on every pull and push only to be dropped.");
        output.Line(SplitNote, Output.Dim);
        return ExitCodes.Success;
    }

    private static int Offline(Output output, IReadOnlyList<Include> includes)
    {
        var findings = includes.Select(i => (Include: i, Rules: ReadCost.Inspect(i))).Where(f => f.Rules.Count > 0).ToList();
        output.Document("ser analyze", w =>
        {
            w.WriteBoolean("offline", true);
            w.WriteStartArray("includes");
            foreach (var (include, rules) in findings)
            {
                w.WriteStartObject();
                WriteInclude(w, include);
                w.WriteStartArray("rules");
                foreach (var r in rules)
                {
                    w.WriteStartObject();
                    WriteRule(w, r.Rule);
                    w.WriteString("advice", r.Advice);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        foreach (var (include, rules) in findings)
        {
            output.Line($"[{include.Module.Namespace}] {include.Name} ({include.Path}, {include.Scope}):", Output.WriteColor);
            foreach (var r in rules)
            {
                output.Line($"  {r.Advice}");
            }
            output.Line();
        }
        if (findings.Count == 0)
        {
            output.Line("No issues detected.", SyncRunner.SummaryColor);
            return ExitCodes.Success;
        }
        output.Line($"{Includes(findings.Count)} {(findings.Count == 1 ? "has" : "have")} rules that make the CM list items only for them to be dropped. What that costs depends on how many items are below them: run `tern ser analyze` without --offline to count them.");
        output.Line(SplitNote, Output.Dim);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Warns, after a pull or push has read the CM, when its Includes read far more than they keep,
    /// so a slow configuration is noticed by the people it slows down.
    /// </summary>
    internal static void WarnIfOverReading(Output output, CmItemTree cm, IReadOnlyList<Include> includes, string? environment)
    {
        long read = 0, kept = 0;
        var count = 0;
        foreach (var include in includes)
        {
            if (cm.Read(include) is not { } items)
            {
                continue;
            }
            var k = ReadCost.CountKept(include, items);
            if (ReadCost.OverReads(items.Count, k))
            {
                count++;
                read += items.Count;
                kept += k;
            }
        }
        if (count > 0)
        {
            var command = environment is null ? "tern ser analyze" : $"tern ser analyze -n {environment}";
            output.Warn($"{Includes(count)} read {read:N0} items from the CM to keep {kept:N0}. Run `{command}` to see which rules drop the rest, and how to stop reading them.");
        }
    }

    private const string SplitNote = "Include names are folder names, so splitting an include moves its Item files: pull once after the change and commit the moved files.";

    private static string Includes(int count) => count == 1 ? "1 include" : $"{count:N0} includes";

    private static string Ratio(long read, long kept) => kept == 0 ? string.Empty : $" ({(double)read / kept:#,0.#} times as many)";

    private static void WriteInclude(System.Text.Json.Utf8JsonWriter w, Include include)
    {
        w.WriteString("module", include.Module.Namespace);
        w.WriteString("include", include.Name);
        w.WriteString("path", include.Path.ToString());
        w.WriteString("database", include.Database);
        w.WriteString("scope", include.Scope.ToString());
    }

    private static void WriteRule(System.Text.Json.Utf8JsonWriter w, IncludeRule? rule)
    {
        if (rule is null)
        {
            w.WriteNull("rule");
            return;
        }
        w.WriteString("rule", rule.Path);
        w.WriteString("ruleScope", rule.Scope?.ToString());
    }
}
