using Tern.Cli.Common;

namespace Tern.Core.Tests.Configuration;

public class SettingsTests
{
    [Theory]
    [InlineData("https://xmcloudcm.localhost", 0)]
    [InlineData("https://localhost:8443/", 0)]
    [InlineData("https://127.0.0.1", 0)]
    [InlineData("https://[::1]", 0)]
    [InlineData("https://xmc-example-dev.sitecorecloud.io/", 200)]
    [InlineData("https://cm.example.com", 200)]
    public void Subtrees_are_split_only_on_a_remote_cm(string host, int threshold) =>
        Assert.Equal(threshold, Session.DefaultSplitThreshold(host));
}
