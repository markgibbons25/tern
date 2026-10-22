using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Tern.Core.Items;

namespace Tern.Core.Signatures;

/// <summary>
/// Computes an item's Signature exactly as Management Services does, so a client-side Signature
/// can be compared with the one a CM reports. Name and parent are not part of it; renames and
/// moves are detected separately.
/// </summary>
public static class SignatureGenerator
{
    // Management Services sorts languages with the default culture-sensitive comparer.
    private static readonly StringComparer LanguageComparer = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: false);

    public static string Create(ItemData item, bool forceBlobRestore = false) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(CreateBase(item, forceBlobRestore))));

    public static string CreateBase(ItemData item, bool forceBlobRestore = false)
    {
        var sb = new StringBuilder();
        sb.Append(Format(item.TemplateId));
        foreach (var field in item.SharedFields)
        {
            Append(sb, field, forceBlobRestore);
        }
        foreach (var language in item.Languages.Where(l => l.UnversionedFields.Count > 0).OrderBy(l => l.Name, LanguageComparer))
        {
            sb.Append(language.Name);
            foreach (var field in language.UnversionedFields)
            {
                Append(sb, field, forceBlobRestore);
            }
        }
        foreach (var (language, version) in item.AllVersions
                     .OrderBy(x => x.Language.Name, LanguageComparer)
                     .ThenBy(x => x.Version.Number))
        {
            sb.Append(language.Name);
            sb.Append(version.Number.ToString(CultureInfo.InvariantCulture));
            foreach (var field in version.Fields)
            {
                Append(sb, field, forceBlobRestore);
            }
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, FieldValue field, bool forceBlobRestore)
    {
        if (WellKnownFields.Statistics.Contains(field.Id))
        {
            return;
        }
        sb.Append(Format(field.Id));
        if (field.BlobId is { } blobId)
        {
            sb.Append(Format(blobId));
            if (forceBlobRestore)
            {
                sb.Append(string.IsNullOrEmpty(field.Value) ? "blobNotExists" : field.Value);
            }
        }
        else
        {
            foreach (var c in field.Value)
            {
                if (c is not ('\r' or '\n'))
                {
                    sb.Append(c);
                }
            }
        }
    }

    private static string Format(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}
