using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Tern.Core.Configuration;
using Tern.Core.ItemFiles;

namespace Tern.Core.Security;

/// <summary>A role: <c>domain\name</c> and the roles it is a member of.</summary>
public sealed record RoleData(string RoleName, IReadOnlyList<string> MemberOfRoles, string? File = null)
{
    // Role and user names are matched the way the Sitecore CLI matches them, culture-aware, so both tools pair the same files.
#pragma warning disable CA1309
    public bool SameAs(RoleData other) =>
        RoleName.Equals(other.RoleName, StringComparison.InvariantCultureIgnoreCase) &&
        MemberOfRoles.SequenceEqual(other.MemberOfRoles, StringComparer.InvariantCultureIgnoreCase);
#pragma warning restore CA1309
}

public sealed record ProfileProperty(string Name, string Content, string ContentType, bool IsCustomProperty);

public sealed record UserData(string UserName, string? Email, string? Comment, DateTime CreationDate, bool IsApproved, IReadOnlyList<ProfileProperty> Properties, IReadOnlyList<string> Roles, string? File = null)
{
    // Role and user names are matched the way the Sitecore CLI matches them, culture-aware, so both tools pair the same files.
#pragma warning disable CA1309
    public bool SameAs(UserData other) =>
        UserName.Equals(other.UserName, StringComparison.InvariantCultureIgnoreCase) &&
        string.Equals(Email, other.Email, StringComparison.InvariantCultureIgnoreCase) &&
        IsApproved == other.IsApproved &&
        string.Equals(Comment, other.Comment, StringComparison.InvariantCulture) &&
        Roles.Order(StringComparer.Ordinal).SequenceEqual(other.Roles.Order(StringComparer.Ordinal)) &&
        Properties.OrderBy(p => p.Name, StringComparer.Ordinal).SequenceEqual(other.Properties.OrderBy(p => p.Name, StringComparer.Ordinal));
#pragma warning restore CA1309
}

/// <summary>
/// Which roles or users a Module serializes: a domain plus a case-insensitive regular expression
/// on the name without its domain.
/// </summary>
public static class SecurityPredicates
{
    public static bool Includes(IEnumerable<SecurityPattern> patterns, string fullName)
    {
        var parts = fullName.Split('\\');
        var domain = parts[0];
        var name = parts[^1];
        return patterns.Any(p => !string.IsNullOrWhiteSpace(p.Domain) && !string.IsNullOrWhiteSpace(p.Pattern) &&
                                 domain.Equals(p.Domain, StringComparison.OrdinalIgnoreCase) &&
                                 Regex.IsMatch(name, p.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }
}

/// <summary>Role and user files, in the Official CLI's formats and folders.</summary>
public static class SecurityFiles
{
    public static string RolesFolder(Module module, RootConfiguration root) =>
        Path.Combine(Path.GetDirectoryName(module.SourcePath)!, root.DefaultModuleRelativeSerializationPath, "_roles");

    public static string UsersFolder(Module module, RootConfiguration root)
    {
        var relative = string.IsNullOrWhiteSpace(module.ItemsPath) ? root.DefaultModuleRelativeSerializationPath : module.ItemsPath;
        if (relative.StartsWith('~'))
        {
            return Path.Combine(root.RootDirectory, (relative.Length > 2 ? relative[2..] : string.Empty).Replace("$(module)", module.Namespace), "_users");
        }
        return Path.Combine(Path.GetDirectoryName(module.SourcePath)!, relative, "_users");
    }

    /// <summary>Where a role's file goes. The name's backslash becomes a folder on Windows, as it does for the Official CLI.</summary>
    public static string RoleFile(string folder, string roleName) => Inside(folder, roleName + ".yml");

    /// <summary>Where a user's file goes, the same way as <see cref="RoleFile"/>.</summary>
    public static string UserFile(string folder, string userName) => Inside(folder, userName + ".yml");

    /// <summary>
    /// <paramref name="relative"/> under <paramref name="folder"/>. Role and user names come from the
    /// CM, so a name that is rooted or climbs out with <c>..</c> is refused rather than written outside it.
    /// </summary>
    private static string Inside(string folder, string relative)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var full = Path.GetFullPath(Path.Join(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The name '{relative[..^4]}' would put its file outside {folder}.");
        }
        return Path.Join(folder, relative);
    }

    public static RoleData ReadRole(string file)
    {
        var p = new ItemFileReader.LineParser(Decode(File.ReadAllBytes(file)), lenient: true);
        if (!p.AdvanceToDocument())
        {
            throw new ItemFileFormatException($"Error parsing YAML {file}: Invalid YAML format, missing expected --- header.");
        }
        var name = p.Expected("Role");
        var members = new List<string>();
        if (p.Optional("MemberOf", out _))
        {
            while (p.Optional("Role", out var member))
            {
                members.Add(member);
            }
        }
        return new RoleData(name, members, file);
    }

    public static byte[] WriteRole(RoleData role, string? newLine = null)
    {
        var w = new ItemFileWriter.Writer(newLine ?? Environment.NewLine);
        w.Line("---");
        w.Map("Role", role.RoleName);
        if (role.MemberOfRoles.Count > 0)
        {
            w.Key("MemberOf");
            w.Indent += 2;
            foreach (var m in role.MemberOfRoles)
            {
                w.Map("Role", m);
            }
            w.Indent -= 2;
        }
        return Encode(w.ToString());
    }

    public static UserData ReadUser(string file)
    {
        var p = new ItemFileReader.LineParser(Decode(File.ReadAllBytes(file)), lenient: true);
        if (!p.AdvanceToDocument())
        {
            throw new ItemFileFormatException($"Error parsing YAML {file}: Invalid YAML format, missing expected --- header.");
        }
        var userName = p.Expected("UserName");
        string? email = p.Optional("Email", out var e) ? e : null;
        string? comment = p.Optional("Comment", out var c) ? c : null;
        var created = DateTime.Parse(p.Expected("Created"), CultureInfo.InvariantCulture);
        var approved = bool.Parse(p.Expected("IsApproved"));
        var properties = new List<ProfileProperty>();
        if (p.Optional("Properties", out _))
        {
            while (p.Peek() is { Key: "Key" })
            {
                properties.Add(new ProfileProperty(p.Expected("Key"), p.Expected("Value"), p.Expected("ValueType"), bool.Parse(p.Expected("IsCustomProperty"))));
            }
        }
        var roles = new List<string>();
        if (p.Optional("Roles", out _))
        {
            while (p.Optional("MemberOf", out var role))
            {
                roles.Add(role);
            }
        }
        return new UserData(userName, email, comment, created, approved, properties, roles, file);
    }

    public static byte[] WriteUser(UserData user, string? newLine = null)
    {
        var w = new ItemFileWriter.Writer(newLine ?? Environment.NewLine);
        w.Line("---");
        w.Map("UserName", user.UserName);
        if (user.Email is not null)
        {
            w.Map("Email", user.Email);
        }
        if (user.Comment is not null)
        {
            w.Map("Comment", user.Comment);
        }
        w.Map("Created", user.CreationDate.ToString(CultureInfo.InvariantCulture));
        w.Map("IsApproved", user.IsApproved.ToString());
        if (user.Properties.Count > 0)
        {
            w.Key("Properties");
            w.Indent += 2;
            foreach (var prop in user.Properties)
            {
                w.ListItem("Key", prop.Name);
                w.Map("Value", prop.Content);
                w.Map("ValueType", prop.ContentType);
                w.Map("IsCustomProperty", prop.IsCustomProperty.ToString());
            }
            w.Indent -= 2;
        }
        if (user.Roles.Count > 0)
        {
            w.Key("Roles");
            w.Indent += 2;
            foreach (var r in user.Roles)
            {
                w.Map("MemberOf", r);
            }
            w.Indent -= 2;
        }
        return Encode(w.ToString());
    }

    public static void ValidateRoleName(string roleName)
    {
        var parts = roleName.Trim().Split('\\');
        if (parts.Length != 2)
        {
            throw new ConfigurationException($"RolePredicateItem: \"RoleName\" is invalid. \"[{roleName}]\" should match the pattern: domain\\role");
        }
        if (!Regex.IsMatch(parts[0], "^[a-zA-Z0-9]+$"))
        {
            throw new ConfigurationException("RolePredicateItem: The domain name can only contain the following characters: A-Z, a-z, 0-9 and cannot contain spaces.");
        }
        if (!Regex.IsMatch(parts[1], "^[a-zA-Z0-9&\\s]+$"))
        {
            throw new ConfigurationException("RolePredicateItem: The role name can only contain the following characters: A-Z, a-z, 0-9, ampersand and space.");
        }
    }

    private static string Decode(byte[] bytes) =>
        bytes.AsSpan().StartsWith("﻿"u8) ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);

    private static byte[] Encode(string text) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
}
