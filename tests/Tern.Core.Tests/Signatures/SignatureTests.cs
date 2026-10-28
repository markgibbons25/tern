using Tern.Core.ItemFiles;
using Tern.Core.Items;
using Tern.Core.Signatures;

namespace Tern.Core.Tests.Signatures;

public class SignatureTests
{
    private static string Fixture(params string[] parts) =>
        Path.Join([AppContext.BaseDirectory, "fixtures", "push-basic", "s0", "items", "tern-tests", .. parts]);

    /// <summary>
    /// The expected values are the <c>dataSignature</c>s a local SitecoreAI CM (Management Services
    /// 5.2.130) reported for these fixture items after they were pushed, so the test pins Tern's
    /// algorithm to the server's.
    /// </summary>
    [Theory]
    [InlineData("vtJKxh4XvDNQb5rNoRUL4IxdOfO51BQd24eMVtAHH/Q=", "Tern Tests", "Alpha.yml")]
    [InlineData("ZpgpBO3CwCaYwVkcrMbWHKgTbwKqqlwx5WHtRVJQ7z0=", "Tern Tests", "Beta.yml")]
    [InlineData("35DeAZgafU7UdkL8AOedPSrnffBBiiuqM9N+BkqkdZ0=", "Tern Tests", "Gamma", "Delta.yml")]
    [InlineData("fHkgOyFpT9E2yjQHuVFyNozaB85GrVWVXocFXUgxaOs=", "Tern Tests.yml")]
    public void Matches_the_signature_Management_Services_computes(string expected, params string[] file)
    {
        var item = ItemFileReader.Read(File.ReadAllBytes(Fixture(file)));
        Assert.Equal(expected, SignatureGenerator.Create(item));
    }

    [Fact]
    public void Ignores_statistics_fields_and_line_breaks()
    {
        var a = new ItemData { Id = Guid.NewGuid(), ParentId = Guid.Empty, TemplateId = Guid.Empty, Path = "/x" };
        var b = new ItemData { Id = a.Id, ParentId = Guid.Empty, TemplateId = Guid.Empty, Path = "/y" };
        a.SharedFields.Add(new FieldValue(Guid.Parse("00000000-0000-0000-0000-000000000001"), null, null, "one\r\ntwo"));
        b.SharedFields.Add(new FieldValue(Guid.Parse("00000000-0000-0000-0000-000000000001"), null, null, "onetwo"));
        b.SharedFields.Add(new FieldValue(WellKnownFields.Revision, null, null, Guid.NewGuid().ToString()));
        Assert.Equal(SignatureGenerator.Create(a), SignatureGenerator.Create(b));
    }

    [Fact]
    public void A_blob_counts_by_its_id_not_its_content()
    {
        var blob = Guid.NewGuid();
        var a = new ItemData { Id = Guid.NewGuid(), ParentId = Guid.Empty, TemplateId = Guid.Empty, Path = "/x" };
        var b = new ItemData { Id = a.Id, ParentId = Guid.Empty, TemplateId = Guid.Empty, Path = "/x" };
        a.SharedFields.Add(new FieldValue(Guid.NewGuid(), "Blob", blob, "AAAA"));
        b.SharedFields.Add(new FieldValue(a.SharedFields.Single().Id, "Blob", blob, "BBBB"));
        Assert.Equal(SignatureGenerator.Create(a), SignatureGenerator.Create(b));
    }
}
