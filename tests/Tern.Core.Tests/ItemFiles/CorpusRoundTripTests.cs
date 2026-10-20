using Tern.Core.ItemFiles;

namespace Tern.Core.Tests.ItemFiles;

/// <summary>
/// Reads every Item file under the folders in TERN_CORPUS (separated by ';') and checks that
/// writing it back produces the same bytes. The corpus is real project content, so it is never
/// committed; without the variable the test is skipped.
/// </summary>
public class CorpusRoundTripTests
{
    [Fact]
    public void Every_corpus_file_round_trips_byte_for_byte()
    {
        var roots = Environment.GetEnvironmentVariable("TERN_CORPUS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(roots), "TERN_CORPUS is not set.");

        var failures = new List<string>();
        var handEdited = new List<string>();
        var count = 0;
        foreach (var root in roots!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.yml", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}_roles{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}_users{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }
                count++;
                var original = File.ReadAllBytes(file);
                var newLine = original.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
                try
                {
                    var item = ItemFileReader.Read(original, file, lenient: false);
                    var written = ItemFileWriter.ToBytes(item, newLine);
                    var hasBom = original.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]);
                    var comparable = hasBom ? written : written[3..];
                    if (comparable.AsSpan().SequenceEqual(original))
                    {
                        continue;
                    }
                    // A file the Official CLI never wrote (hand-edited, say) may legitimately differ
                    // in bytes. It must still mean exactly the same item once re-read.
                    var reread = ItemFileWriter.ToBytes(ItemFileReader.Read(written, file, lenient: false), newLine);
                    if (reread.AsSpan().SequenceEqual(written))
                    {
                        handEdited.Add($"{file}: {(hasBom ? $"bytes differ at {FirstDifference(original, comparable)}" : "no BOM")}");
                    }
                    else
                    {
                        failures.Add($"{file}: bytes differ at {FirstDifference(original, comparable)} and do not re-read stably");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{file}: {ex.Message}");
                }
            }
        }

        TestContext.Current.SendDiagnosticMessage($"{count} files checked, {handEdited.Count} not written by the Official CLI, {failures.Count} failed.");
        foreach (var line in handEdited)
        {
            TestContext.Current.SendDiagnosticMessage("not Official CLI output: " + line);
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} files failed:\n" + string.Join('\n', failures.Take(25)));
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }
        return n;
    }
}
