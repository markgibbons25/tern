using Tern.Cli.Common;

namespace Tern.Core.Tests.Cli;

public class UpdateCheckTests
{
    [Theory]
    [InlineData("0.1.10", "0.1.9", true)]
    [InlineData("0.2.0", "0.1.6", true)]
    [InlineData("0.1.6", "0.1.6", false)]
    [InlineData("0.1.5", "0.1.6", false)]
    [InlineData("0.1.6", "0.1.6-beta.1", true)]
    [InlineData("0.1.6-beta.2", "0.1.6-beta.1", true)]
    [InlineData("0.1.6-beta.1", "0.1.6", false)]
    [InlineData("not-a-version", "0.1.6", false)]
    public void Compares_versions_by_number_not_text(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateCheck.IsNewer(candidate, current));

    [Fact]
    public void The_latest_leaves_out_prereleases_unless_asked()
    {
        string[] versions = ["0.1.9", "0.1.10", "0.2.0-beta.1"];
        Assert.Equal("0.1.10", UpdateCheck.Latest(versions, includePrerelease: false));
        Assert.Equal("0.2.0-beta.1", UpdateCheck.Latest(versions, includePrerelease: true));
    }

    [Fact]
    public void A_check_is_fresh_for_a_day()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(UpdateCheck.IsFresh(now.AddHours(-23), now));
        Assert.False(UpdateCheck.IsFresh(now.AddHours(-25), now));
        // A clock that went backwards doesn't keep a check fresh for ever.
        Assert.False(UpdateCheck.IsFresh(now.AddDays(3), now));
    }

    [Fact]
    public void The_cache_reads_back_what_was_written_and_a_damaged_one_is_ignored()
    {
        var file = Path.Join(Path.GetTempPath(), $"tern-update-{Guid.NewGuid():N}", "update-check.json");
        try
        {
            var at = new DateTimeOffset(2026, 10, 27, 1, 2, 3, TimeSpan.Zero);
            UpdateCheck.WriteCache(file, at, "0.1.7");
            Assert.Equal((at, "0.1.7"), UpdateCheck.ReadCache(file));

            File.WriteAllText(file, "{ not json");
            Assert.Null(UpdateCheck.ReadCache(file));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
