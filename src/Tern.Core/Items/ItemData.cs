namespace Tern.Core.Items;

/// <summary>
/// One Sitecore item with all of its field values, as held in an Item file or returned by a CM.
/// Collections keep the Official CLI's ordering and duplicate rules: fields by GUID, languages by
/// ordinal name, versions by number, and the first of any duplicate wins.
/// </summary>
public sealed class ItemData
{
    public required Guid Id { get; init; }
    public required Guid ParentId { get; init; }
    public required Guid TemplateId { get; set; }
    public required string Path { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>The database name, written only when an Item file carries one.</summary>
    public string? Database { get; set; }

    public FieldList SharedFields { get; } = new();
    public LanguageList Languages { get; } = new();

    /// <summary>The last path segment.</summary>
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash < 0 ? trimmed : trimmed[(slash + 1)..];
        }
    }

    public IEnumerable<(ItemLanguage Language, ItemVersion Version)> AllVersions =>
        Languages.SelectMany(l => l.Versions.Select(v => (l, v)));

    public IEnumerable<FieldValue> AllFields =>
        SharedFields
            .Concat(Languages.SelectMany(l => l.UnversionedFields))
            .Concat(Languages.SelectMany(l => l.Versions.SelectMany(v => v.Fields)));
}

public sealed record FieldValue(Guid Id, string? Hint, Guid? BlobId, string Value);

public sealed class ItemLanguage(string name)
{
    public string Name { get; } = name;
    public FieldList UnversionedFields { get; } = new();
    public VersionList Versions { get; } = new();
}

public sealed class ItemVersion(int number)
{
    public int Number { get; } = number;
    public FieldList Fields { get; } = new();
}

/// <summary>Fields ordered by GUID. Adding a field whose ID is already present is ignored.</summary>
public sealed class FieldList : IReadOnlyCollection<FieldValue>
{
    private readonly SortedDictionary<Guid, FieldValue> _fields = new();

    public int Count => _fields.Count;

    public bool Add(FieldValue field) => _fields.TryAdd(field.Id, field);

    public void Set(FieldValue field) => _fields[field.Id] = field;

    public bool Remove(Guid id) => _fields.Remove(id);

    public FieldValue? this[Guid id] => _fields.GetValueOrDefault(id);

    public IEnumerator<FieldValue> GetEnumerator() => _fields.Values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Languages ordered by ordinal name. Adding a language that is already present is ignored.</summary>
public sealed class LanguageList : IReadOnlyCollection<ItemLanguage>
{
    private readonly SortedDictionary<string, ItemLanguage> _languages = new(StringComparer.Ordinal);

    public int Count => _languages.Count;

    public bool Add(ItemLanguage language) => _languages.TryAdd(language.Name, language);

    public ItemLanguage GetOrAdd(string name)
    {
        if (!_languages.TryGetValue(name, out var language))
        {
            language = new ItemLanguage(name);
            _languages.Add(name, language);
        }
        return language;
    }

    public bool Remove(string name) => _languages.Remove(name);

    public ItemLanguage? this[string name] => _languages.GetValueOrDefault(name);

    public IEnumerator<ItemLanguage> GetEnumerator() => _languages.Values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Versions ordered by number. Adding a version that is already present is ignored.</summary>
public sealed class VersionList : IReadOnlyCollection<ItemVersion>
{
    private readonly SortedDictionary<int, ItemVersion> _versions = new();

    public int Count => _versions.Count;

    public bool Add(ItemVersion version) => _versions.TryAdd(version.Number, version);

    public ItemVersion GetOrAdd(int number)
    {
        if (!_versions.TryGetValue(number, out var version))
        {
            version = new ItemVersion(number);
            _versions.Add(number, version);
        }
        return version;
    }

    public bool Remove(int number) => _versions.Remove(number);

    public ItemVersion? this[int number] => _versions.GetValueOrDefault(number);

    public IEnumerator<ItemVersion> GetEnumerator() => _versions.Values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
