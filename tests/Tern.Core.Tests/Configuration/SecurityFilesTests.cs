using Tern.Core.Security;

namespace Tern.Core.Tests.Configuration;

public class SecurityFilesTests
{
    private static readonly string Folder = Path.Join(Path.GetTempPath(), "tern-roles");

    [Fact]
    public void A_role_file_is_named_after_the_role()
    {
        Assert.Equal(Path.Join(Folder, @"sitecore\Author.yml"), SecurityFiles.RoleFile(Folder, @"sitecore\Author"));
        Assert.Equal(Path.Join(Folder, @"extranet\Anonymous.yml"), SecurityFiles.UserFile(Folder, @"extranet\Anonymous"));
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("sitecore/../../escape")]
    public void A_name_from_the_CM_cannot_put_its_file_outside_the_folder(string name)
    {
        Assert.Throws<InvalidDataException>(() => SecurityFiles.RoleFile(Folder, name));
        Assert.Throws<InvalidDataException>(() => SecurityFiles.UserFile(Folder, name));
    }

    [Fact]
    public void A_rooted_name_is_refused()
    {
        var rooted = Path.Join(Path.GetTempPath(), "elsewhere", "role");
        Assert.Throws<InvalidDataException>(() => SecurityFiles.RoleFile(Folder, rooted));
    }
}
