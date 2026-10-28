using System.Security.Cryptography;
using System.Text;

namespace Tern.Core.Trees;

/// <summary>
/// What Tern last read from each Item file of a folder: its header and Signature, keyed by the
/// file's size and last write time. A file that hasn't changed is not opened again, which is most
/// of the cost of reading a large project (on Windows, opening a file costs more than parsing it).
/// The Official CLI's <c>.scindex</c> does the same job, but its Signatures don't record which
/// fields were excluded, so Tern keeps its own index outside the project, one per folder and
/// excluded-field set. The index is only a cache: anything wrong with it means the files are read.
/// </summary>
public sealed class FileIndexCache(string directory, string key)
{
    private const string Magic = "TERNIDX1";

    public sealed record Entry(long Size, long Modified, Guid Id, Guid ParentId, Guid TemplateId, string Path, string Signature);

    /// <summary>The user's index folder: <c>%LOCALAPPDATA%\Tern\file-index</c> or <c>~/.local/share/Tern/file-index</c>.</summary>
    public static FileIndexCache ForUser(string key) =>
        new(System.IO.Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tern", "file-index"), key);

    /// <summary>The entries for <paramref name="root"/>, by path relative to it; empty when there is no usable index.</summary>
    public Dictionary<string, Entry> Load(string root)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            var file = FileFor(root);
            if (!File.Exists(file))
            {
                return entries;
            }
            using var reader = new BinaryReader(File.OpenRead(file), Encoding.UTF8);
            if (reader.ReadString() != Magic || reader.ReadString() != Identity(root))
            {
                return entries;
            }
            var count = reader.ReadInt32();
            for (var n = 0; n < count; n++)
            {
                var relative = reader.ReadString();
                entries[relative] = new Entry(reader.ReadInt64(), reader.ReadInt64(), new Guid(reader.ReadBytes(16)), new Guid(reader.ReadBytes(16)), new Guid(reader.ReadBytes(16)), reader.ReadString(), reader.ReadString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException or ArgumentException)
        {
            entries.Clear();
        }
        return entries;
    }

    /// <summary>Replaces the index for <paramref name="root"/>. Failing to write it is not an error.</summary>
    public void Save(string root, IReadOnlyDictionary<string, Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var file = FileFor(root);
            var temp = file + "." + Environment.ProcessId + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temp), Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Identity(root));
                writer.Write(entries.Count);
                foreach (var (relative, e) in entries)
                {
                    writer.Write(relative);
                    writer.Write(e.Size);
                    writer.Write(e.Modified);
                    writer.Write(e.Id.ToByteArray());
                    writer.Write(e.ParentId.ToByteArray());
                    writer.Write(e.TemplateId.ToByteArray());
                    writer.Write(e.Path);
                    writer.Write(e.Signature);
                }
            }
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index only saves time; without it the next run reads the files again.
        }
    }

    // The folder and the excluded-field set together; stored in the file too, so a hash clash can't mix them up.
    private string Identity(string root) => System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + "\n" + key;

    private string FileFor(string root) =>
        System.IO.Path.Join(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Identity(root).ToLowerInvariant())))[..32] + ".idx");
}
