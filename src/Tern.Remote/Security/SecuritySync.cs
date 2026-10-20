using System.Globalization;
using System.Text.Json;
using Tern.Core.Configuration;
using Tern.Core.Security;
using Tern.Remote.Management;

namespace Tern.Remote.Security;

/// <summary>One change to a role or user, and how to describe it.</summary>
public sealed record SecurityChange(string Message, Func<CancellationToken, Task> Apply);

/// <summary>The roles and users queries and mutations of Management Services.</summary>
public sealed class SecurityApi(ManagementClient client)
{
    public async Task<List<RoleData>> RolesAsync(IEnumerable<SecurityPattern> patterns, CancellationToken ct)
    {
        const string query = "query($predicates: [PredicateGraphType]) { roles(predicates: $predicates) { roleName memberOfRoles serializedItemId } }";
        using var doc = await client.QueryAsync(query, w => WritePredicates(w, patterns), ct);
        return doc.RootElement.GetProperty("data").GetProperty("roles").EnumerateArray()
            .Select(r => new RoleData(r.GetProperty("roleName").GetString()!, Strings(r, "memberOfRoles")))
            .ToList();
    }

    public async Task PushRolesAsync(IReadOnlyList<(string Command, RoleData Role, RoleData? Parent)> commands, CancellationToken ct)
    {
        if (commands.Count == 0)
        {
            return;
        }
        const string mutation = "mutation($commands: [RoleCommandGraphType]) { pushCommands(commands: $commands) { success name messages { logLevel eventID { id name } message } } }";
        using var doc = await client.QueryAsync(mutation, w =>
        {
            w.WriteStartArray("commands");
            foreach (var (command, role, parent) in commands)
            {
                w.WriteStartObject();
                w.WritePropertyName("roleData");
                WriteRole(w, role);
                if (parent is not null)
                {
                    w.WritePropertyName("parentRoleData");
                    WriteRole(w, parent);
                }
                w.WriteString("roleCommandType", command);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }, ct);
        ThrowOnFailure(doc.RootElement.GetProperty("data").GetProperty("pushCommands"));
    }

    public async Task<List<UserData>> UsersAsync(IEnumerable<SecurityPattern> patterns, CancellationToken ct)
    {
        const string query = "query($predicates: [Predicate]) { users(predicates: $predicates) { email userName comment creationDate isApproved profileProperties { content contentType isCustomProperty name } roles } }";
        using var doc = await client.QueryAsync(query, w => WritePredicates(w, patterns), ct);
        return doc.RootElement.GetProperty("data").GetProperty("users").EnumerateArray().Select(u => new UserData(
            u.GetProperty("userName").GetString()!,
            u.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
            u.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
            u.TryGetProperty("creationDate", out var d) && d.ValueKind == JsonValueKind.String ? DateTime.Parse(d.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : default,
            u.TryGetProperty("isApproved", out var a) && a.ValueKind == JsonValueKind.True,
            u.TryGetProperty("profileProperties", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => new ProfileProperty(x.GetProperty("name").GetString()!, x.GetProperty("content").GetString() ?? string.Empty, x.GetProperty("contentType").GetString() ?? string.Empty, x.GetProperty("isCustomProperty").GetBoolean())).ToList()
                : [],
            Strings(u, "roles"))).ToList();
    }

    public async Task PushUsersAsync(IReadOnlyList<(string Command, UserData User)> commands, CancellationToken ct)
    {
        if (commands.Count == 0)
        {
            return;
        }
        const string mutation = "mutation($commands: [UserCommand]) { pushUsers(commands: $commands) { success name messages { logLevel eventID { id name } message } } }";
        using var doc = await client.QueryAsync(mutation, w =>
        {
            w.WriteStartArray("commands");
            foreach (var (command, user) in commands)
            {
                w.WriteStartObject();
                w.WriteStartObject("userData");
                w.WriteString("userName", user.UserName);
                PublishingApi.WriteNullable(w, "email", user.Email);
                PublishingApi.WriteNullable(w, "comment", user.Comment);
                w.WriteBoolean("isApproved", user.IsApproved);
                w.WriteString("creationDate", user.CreationDate.ToString("O", CultureInfo.InvariantCulture));
                PublishingApi.WriteArray(w, "roles", user.Roles);
                WriteProperties(w, "profileProperties", user.Properties.Where(x => !x.IsCustomProperty));
                WriteProperties(w, "customProfileProperties", user.Properties.Where(x => x.IsCustomProperty));
                PublishingApi.WriteNullable(w, "serializedItemId", user.File);
                w.WriteEndObject();
                w.WriteString("userCommandType", command);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }, ct);
        ThrowOnFailure(doc.RootElement.GetProperty("data").GetProperty("pushUsers"));
    }

    private static void WriteProperties(Utf8JsonWriter w, string name, IEnumerable<ProfileProperty> properties)
    {
        w.WriteStartArray(name);
        foreach (var p in properties)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("content", p.Content);
            w.WriteString("contentType", p.ContentType);
            w.WriteBoolean("isCustomProperty", p.IsCustomProperty);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteRole(Utf8JsonWriter w, RoleData role)
    {
        w.WriteStartObject();
        w.WriteString("roleName", role.RoleName);
        PublishingApi.WriteArray(w, "memberOfRoles", role.MemberOfRoles);
        PublishingApi.WriteNullable(w, "serializedItemId", role.File);
        w.WriteEndObject();
    }

    private static void WritePredicates(Utf8JsonWriter w, IEnumerable<SecurityPattern> patterns)
    {
        w.WriteStartArray("predicates");
        foreach (var p in patterns)
        {
            w.WriteStartObject();
            w.WriteString("domain", p.Domain);
            w.WriteString("pattern", p.Pattern);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void ThrowOnFailure(JsonElement results)
    {
        var failures = results.EnumerateArray()
            .Where(r => !r.GetProperty("success").GetBoolean())
            .Select(r => $"{r.GetProperty("name").GetString()}: {string.Join("; ", r.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("message").GetString()))}")
            .ToList();
        if (failures.Count > 0)
        {
            throw new ManagementException("The CM rejected a role or user change: " + string.Join(Environment.NewLine, failures));
        }
    }

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
}

/// <summary>
/// Works out role and user changes for a Pull or a Push, Module by Module, with the Official CLI's
/// rules: files are matched by path, then by name; orphans are removed when the root
/// configuration says so.
/// </summary>
public sealed class SecuritySync(SecurityApi api, RootConfiguration root)
{
    public async Task<List<SecurityChange>> PlanRolePullAsync(IReadOnlyList<Module> modules, CancellationToken ct)
    {
        EnsureUniquePredicates(modules, m => m.Roles, "role");
        var changes = new List<SecurityChange>();
        foreach (var module in modules.Where(m => m.Roles.Any(r => !string.IsNullOrWhiteSpace(r.Domain) && !string.IsNullOrWhiteSpace(r.Pattern))))
        {
            var source = (await api.RolesAsync(module.Roles, ct)).Where(r => SecurityPredicates.Includes(module.Roles, r.RoleName)).ToList();
            var folder = SecurityFiles.RolesFolder(module, root);
            var existing = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.yml", SearchOption.AllDirectories).Select(SecurityFiles.ReadRole).ToList() : [];
            foreach (var role in source)
            {
                var path = SecurityFiles.RoleFile(folder, role.RoleName);
                var match = existing.FirstOrDefault(r => r.File!.Equals(path, StringComparison.InvariantCultureIgnoreCase)) ?? existing.FirstOrDefault(r => r.RoleName.Equals(role.RoleName, StringComparison.InvariantCultureIgnoreCase));
                if (match is not null && Path.GetDirectoryName(match.File)!.Equals(Path.GetDirectoryName(path), StringComparison.InvariantCultureIgnoreCase))
                {
                    existing.Remove(match);
                    var samePath = match.File!.Equals(path, StringComparison.InvariantCultureIgnoreCase);
                    if (!match.SameAs(role) || !samePath)
                    {
                        changes.Add(new SecurityChange($"[roles] [U] Role {role.RoleName}", _ => WriteFile(path, SecurityFiles.WriteRole(role), samePath ? null : match.File)));
                    }
                }
                else
                {
                    changes.Add(new SecurityChange($"[roles] [A] Role {role.RoleName}", _ => WriteFile(path, SecurityFiles.WriteRole(role), null)));
                }
            }
            if (root.RemoveOrphansForRoles)
            {
                foreach (var orphan in existing)
                {
                    changes.Add(new SecurityChange($"[roles] [D] Role {orphan.RoleName}", _ =>
                    {
                        File.Delete(orphan.File!);
                        return Task.CompletedTask;
                    }));
                }
            }
        }
        return changes;
    }

    public async Task<List<SecurityChange>> PlanRolePushAsync(IReadOnlyList<Module> modules, CancellationToken ct)
    {
        EnsureUniquePredicates(modules, m => m.Roles, "role");
        var changes = new List<SecurityChange>();
        foreach (var module in modules.Where(m => m.Roles.Any(r => !string.IsNullOrWhiteSpace(r.Domain) && !string.IsNullOrWhiteSpace(r.Pattern))))
        {
            var folder = SecurityFiles.RolesFolder(module, root);
            var source = (Directory.Exists(folder) ? Directory.GetFiles(folder, "*.yml", SearchOption.AllDirectories).Select(SecurityFiles.ReadRole).ToList() : [])
                .Where(r => SecurityPredicates.Includes(module.Roles, r.RoleName)).ToList();
            source.ForEach(r => SecurityFiles.ValidateRoleName(r.RoleName));
            var filtered = (await api.RolesAsync(module.Roles, ct)).ToList();
            var domains = filtered.SelectMany(r => r.MemberOfRoles).Select(n => n.Split('\\')[0]).Concat(module.Roles.Select(r => r.Domain)).Distinct(StringComparer.OrdinalIgnoreCase);
            var snapshot = await api.RolesAsync(domains.Select(d => new SecurityPattern(d, ".")), ct);
            var commands = new List<(string Command, RoleData Role, RoleData? Parent, string Message)>();
            bool Exists(string name) => snapshot.Any(x => x.RoleName.Equals(name, StringComparison.OrdinalIgnoreCase)) || filtered.Any(x => x.RoleName.Equals(name, StringComparison.OrdinalIgnoreCase));

            foreach (var role in source)
            {
                if (!Exists(role.RoleName))
                {
                    var created = new RoleData(role.RoleName, []);
                    commands.Add(("ADD", created, null, $"[roles] [A] Role {role.RoleName}"));
                    filtered.Add(created);
                }
                var target = filtered.First(x => x.RoleName == role.RoleName || x.RoleName.Equals(role.RoleName, StringComparison.OrdinalIgnoreCase));
                foreach (var parent in role.MemberOfRoles)
                {
                    var parentRole = new RoleData(parent, []);
                    if (!Exists(parent))
                    {
                        commands.Add(("ADD", parentRole, null, $"[roles] [A] Role {parent}"));
                        filtered.Add(parentRole);
                    }
                    if (!target.MemberOfRoles.Any(m => m.Contains(parent, StringComparison.Ordinal)))
                    {
                        var updated = target with { MemberOfRoles = [.. target.MemberOfRoles, parent] };
                        commands.Add(("ASSIGN", updated, parentRole, $"[roles] [A] Role {parent} is assigned to {target.RoleName}"));
                        var index = filtered.FindIndex(r => r.RoleName.Equals(target.RoleName, StringComparison.OrdinalIgnoreCase));
                        filtered[index] = updated;
                        target = updated;
                    }
                }
                foreach (var stale in target.MemberOfRoles.Where(p => !role.MemberOfRoles.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    commands.Add(("UNASSIGN", target, new RoleData(stale, []), $"[roles] [D] Role {target.RoleName} is unassigned from {stale}"));
                }
            }
            if (root.RemoveOrphansForRoles)
            {
                var known = source.Select(r => r.RoleName).ToHashSet(StringComparer.Ordinal);
                var candidates = source.SelectMany(r => r.MemberOfRoles).Concat(filtered.Select(r => r.RoleName)).Distinct(StringComparer.Ordinal);
                foreach (var name in candidates.Where(n => SecurityPredicates.Includes(module.Roles, n) && !known.Contains(n)))
                {
                    commands.Add(("REMOVE", new RoleData(name, []), null, $"[roles] [D] Role {name}"));
                }
            }
            if (commands.Count > 0)
            {
                var batch = commands.Select(c => (c.Command, c.Role, c.Parent)).ToList();
                var first = true;
                foreach (var c in commands)
                {
                    var isFirst = first;
                    first = false;
                    changes.Add(new SecurityChange(c.Message, ct2 => isFirst ? api.PushRolesAsync(batch, ct2) : Task.CompletedTask));
                }
            }
        }
        return changes;
    }

    public async Task<List<SecurityChange>> PlanUserPullAsync(IReadOnlyList<Module> modules, CancellationToken ct)
    {
        EnsureUniquePredicates(modules, m => m.Users, "user");
        var changes = new List<SecurityChange>();
        foreach (var module in modules.Where(m => m.Users.Any(u => !string.IsNullOrWhiteSpace(u.Domain) && !string.IsNullOrWhiteSpace(u.Pattern))))
        {
            var source = (await api.UsersAsync(module.Users, ct)).Where(u => SecurityPredicates.Includes(module.Users, u.UserName)).ToList();
            var folder = SecurityFiles.UsersFolder(module, root);
            var existing = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.yml", SearchOption.AllDirectories).Select(SecurityFiles.ReadUser).ToList() : [];
            foreach (var user in source)
            {
                var path = SecurityFiles.UserFile(folder, user.UserName);
                var match = existing.FirstOrDefault(u => u.File!.Equals(path, StringComparison.InvariantCultureIgnoreCase)) ?? existing.FirstOrDefault(u => u.UserName.Equals(user.UserName, StringComparison.InvariantCultureIgnoreCase));
                if (match is not null && Path.GetDirectoryName(match.File)!.Equals(Path.GetDirectoryName(path), StringComparison.InvariantCultureIgnoreCase))
                {
                    existing.Remove(match);
                    var samePath = match.File!.Equals(path, StringComparison.InvariantCultureIgnoreCase);
                    if (!match.SameAs(user) || !samePath)
                    {
                        changes.Add(new SecurityChange($"[users] [U] User {user.UserName}", _ => WriteFile(path, SecurityFiles.WriteUser(user), samePath ? null : match.File)));
                    }
                }
                else
                {
                    changes.Add(new SecurityChange($"[users] [A] User {user.UserName}", _ => WriteFile(path, SecurityFiles.WriteUser(user), null)));
                }
            }
            if (root.RemoveOrphansForUsers)
            {
                foreach (var orphan in existing)
                {
                    changes.Add(new SecurityChange($"[users] [D] User {orphan.UserName}", _ =>
                    {
                        File.Delete(orphan.File!);
                        return Task.CompletedTask;
                    }));
                }
            }
        }
        return changes;
    }

    public async Task<List<SecurityChange>> PlanUserPushAsync(IReadOnlyList<Module> modules, CancellationToken ct)
    {
        EnsureUniquePredicates(modules, m => m.Users, "user");
        var changes = new List<SecurityChange>();
        foreach (var module in modules.Where(m => m.Users.Any(u => !string.IsNullOrWhiteSpace(u.Domain) && !string.IsNullOrWhiteSpace(u.Pattern))))
        {
            var folder = SecurityFiles.UsersFolder(module, root);
            var source = (Directory.Exists(folder) ? Directory.GetFiles(folder, "*.yml", SearchOption.AllDirectories).Select(SecurityFiles.ReadUser).ToList() : [])
                .Where(u => SecurityPredicates.Includes(module.Users, u.UserName)).ToList();
            var target = await api.UsersAsync(module.Users, ct);
            var commands = new List<(string Command, UserData User, string Message)>();
            foreach (var user in source)
            {
                var existing = target.FirstOrDefault(t => t.UserName == user.UserName);
                if (existing is null)
                {
                    commands.Add(("ADD", user, $"[users] [A] User {user.UserName}"));
                }
                else if (!user.SameAs(existing))
                {
                    commands.Add(("UPDATE", user, $"[users] [U] User {user.UserName}"));
                }
            }
            if (root.RemoveOrphansForUsers)
            {
                foreach (var orphan in target.Where(t => SecurityPredicates.Includes(module.Users, t.UserName) && source.All(s => !s.UserName.Equals(t.UserName, StringComparison.OrdinalIgnoreCase))))
                {
                    commands.Add(("REMOVE", orphan, $"[users] [D] User {orphan.UserName}"));
                }
            }
            if (commands.Count > 0)
            {
                var batch = commands.Select(c => (c.Command, c.User)).ToList();
                var first = true;
                foreach (var c in commands)
                {
                    var isFirst = first;
                    first = false;
                    changes.Add(new SecurityChange(c.Message, ct2 => isFirst ? api.PushUsersAsync(batch, ct2) : Task.CompletedTask));
                }
            }
        }
        return changes;
    }

    private static Task WriteFile(string path, byte[] bytes, string? remove)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        if (remove is not null && File.Exists(remove))
        {
            File.Delete(remove);
        }
        return Task.CompletedTask;
    }

    private static void EnsureUniquePredicates(IReadOnlyList<Module> modules, Func<Module, IReadOnlyList<SecurityPattern>> select, string kind)
    {
        var seen = new Dictionary<SecurityPattern, Module>();
        foreach (var module in modules)
        {
            foreach (var p in select(module))
            {
                if (seen.TryGetValue(p, out var other))
                {
                    throw new ConfigurationException($"The {kind} predicate {p.Domain}\\{p.Pattern} was included in multiple places. Found valid {kind}s within these modules:{Environment.NewLine}{module.SourcePath}{Environment.NewLine}{other.SourcePath}");
                }
                seen.Add(p, module);
            }
        }
    }
}
