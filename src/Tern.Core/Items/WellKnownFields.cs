namespace Tern.Core.Items;

public static class WellKnownFields
{
    public static readonly Guid LastRun = new("b1e16562-f3f9-4ddd-84ca-6e099950ecc0");
    public static readonly Guid Revision = new("8cdc337e-a112-42fb-bbb4-4143751e123f");
    public static readonly Guid Owner = new("52807595-0f8f-4b20-8d2a-cb71d28c6103");
    public static readonly Guid Created = new("25bed78c-4957-4165-998a-ca1b52f67497");
    public static readonly Guid CreatedBy = new("5dd74568-4d4b-44c1-b513-0af5f4cda34f");
    public static readonly Guid Updated = new("d9cf14b1-fa16-4ba6-9288-e8a174d4d522");
    public static readonly Guid UpdatedBy = new("badd9cf9-53e0-4d0c-bcc0-2d784c282f6a");
    public static readonly Guid Lock = new("001dd393-96c5-490b-924a-b0f25cd9efd8");

    /// <summary>Fields that change on every save and never count towards a Signature.</summary>
    public static readonly IReadOnlySet<Guid> Statistics = new HashSet<Guid>
    {
        LastRun, Owner, Revision, Updated, UpdatedBy, Lock,
    };
}
