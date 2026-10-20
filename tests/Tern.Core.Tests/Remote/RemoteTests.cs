using System.Text.Json;
using Tern.Core.Items;
using Tern.Remote.Login;
using Tern.Remote.Management;

namespace Tern.Core.Tests.Remote;

public class LoginStoreTests
{
    [Fact]
    public void Keeps_unknown_properties_and_resolves_ref_endpoints()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tern-user-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """
            {
              "endpoints": {
                "xmCloud": { "authority": "https://auth.example/", "accessToken": "old", "custom": { "keep": true } },
                "default": { "ref": "xmcloud", "host": "https://cm.example", "allowWrite": true }
              },
              "defaultEndpoint": "default",
              "somethingNew": 42
            }
            """);
        try
        {
            var store = LoginStore.Load(file);
            var endpoint = store.Find(null)!;
            Assert.Equal("https://cm.example", endpoint.Host);
            Assert.Equal("https://auth.example/", endpoint.Authority);
            endpoint.AccessToken = "new";
            store.Save();

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var cloud = doc.RootElement.GetProperty("endpoints").GetProperty("xmCloud");
            Assert.Equal("new", cloud.GetProperty("accessToken").GetString());
            Assert.True(cloud.GetProperty("custom").GetProperty("keep").GetBoolean());
            Assert.Equal(42, doc.RootElement.GetProperty("somethingNew").GetInt32());
            Assert.False(doc.RootElement.GetProperty("endpoints").GetProperty("default").TryGetProperty("accessToken", out _));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class UpdatePayloadTests
{
    private static ItemData Item()
    {
        return new ItemData { Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), TemplateId = Guid.Parse("76036f5e-cbce-46d1-af0a-4143f9b557aa"), Path = "/x" };
    }

    private static readonly Guid Title = Guid.Parse("75577384-3c97-45da-a847-81b00500e250");
    private static readonly Guid Text = Guid.Parse("a60acd61-a6db-4182-8329-c957982cec74");

    [Fact]
    public void Nothing_to_send_when_items_only_differ_by_line_breaks()
    {
        var source = Item();
        var target = Item();
        source.SharedFields.Add(new FieldValue(Text, null, null, "a\r\nb"));
        target.SharedFields.Add(new FieldValue(Text, null, null, "a\nb"));
        Assert.Null(CmApplier.UpdatePayload(source, target));
    }

    [Fact]
    public void Updates_changed_fields_resets_missing_ones_and_syncs_versions()
    {
        var source = Item();
        var target = Item();
        source.SharedFields.Add(new FieldValue(Title, null, null, "new"));
        target.SharedFields.Add(new FieldValue(Title, null, null, "old"));
        target.SharedFields.Add(new FieldValue(Text, null, null, "gone"));
        source.Languages.GetOrAdd("en").Versions.GetOrAdd(2);
        target.Languages.GetOrAdd("en").Versions.GetOrAdd(1);

        using var doc = JsonDocument.Parse(CmApplier.UpdatePayload(source, target)!);
        var commands = doc.RootElement.EnumerateArray().Select(c => c.GetProperty("command").GetString()).ToList();
        // As the Official CLI orders them: fields only on the target before fields that changed.
        Assert.Equal(["REMOVE_VERSION", "ADD_VERSION", "RESET_FIELD", "UPDATE"], commands);
    }

    [Fact]
    public void Changes_the_template_first()
    {
        var source = Item();
        var target = Item();
        target.TemplateId = Guid.NewGuid();
        using var doc = JsonDocument.Parse(CmApplier.UpdatePayload(source, target)!);
        Assert.Equal("CHANGE_TEMPLATE", doc.RootElement[0].GetProperty("command").GetString());
    }
}

public class SpeTokenTests
{
    [Fact]
    public void Signs_an_HS256_token_SPE_can_verify()
    {
        var token = Tern.Remote.Spe.SpeClient.Token(new Tern.Remote.Spe.SpeCredentials.SharedSecret("sitecore\admin", "secret"), "https://cm.example/", DateTimeOffset.FromUnixTimeSeconds(1_000));
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var signature = System.Security.Cryptography.HMACSHA256.HashData("secret"u8.ToArray(), System.Text.Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]));
        Assert.Equal(Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_'), parts[2]);
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - payload.Length % 4) % 4);
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        Assert.Equal("SPE Remoting", doc.RootElement.GetProperty("iss").GetString());
        Assert.Equal("https://cm.example", doc.RootElement.GetProperty("aud").GetString());
        Assert.Equal(1_030, doc.RootElement.GetProperty("exp").GetInt64());
    }
}
