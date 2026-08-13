using S1Atlas.Extraction.Cleanup;

namespace S1Atlas.Extraction.Tests.Cleanup;

/// <summary>
/// An in-memory <see cref="ICleanupFileSystem"/> for cleanup tests. Ancestors are
/// created automatically, and <see cref="Remove"/> models a real recursive delete so
/// re-observation after an apply reflects the deletion.
/// </summary>
internal sealed class InMemoryCleanupFileSystem : ICleanupFileSystem
{
    private static readonly DateTimeOffset DefaultWrite =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);

    public InMemoryCleanupFileSystem(string dataRoot)
    {
        _dataRoot = dataRoot;
        _nodes[dataRoot] = new Node(FileAttributes.Directory, 0, DefaultWrite, false);
        _children[dataRoot] = [];
    }

    public void Directory(string path, DateTimeOffset lastWrite, bool reparsePoint = false)
    {
        EnsureAncestors(path);
        var attributes = FileAttributes.Directory;
        if (reparsePoint)
        {
            attributes |= FileAttributes.ReparsePoint;
        }

        _nodes[path] = new Node(attributes, 0, lastWrite, false);
        _children.TryAdd(path, []);
    }

    public void File(
        string path,
        long length,
        DateTimeOffset lastWrite,
        bool reparsePoint = false,
        bool unreadable = false)
    {
        EnsureAncestors(path);
        var attributes = FileAttributes.Normal;
        if (reparsePoint)
        {
            attributes |= FileAttributes.ReparsePoint;
        }

        _nodes[path] = new Node(attributes, length, lastWrite, unreadable);
    }

    public void Remove(string path)
    {
        if (!_nodes.ContainsKey(path))
        {
            return;
        }

        if (_children.TryGetValue(path, out var children))
        {
            foreach (var child in children.ToArray())
            {
                Remove(child);
            }

            _children.Remove(path);
        }

        _nodes.Remove(path);
        var parent = Path.GetDirectoryName(path);
        if (parent is not null && _children.TryGetValue(parent, out var siblings))
        {
            siblings.Remove(path);
        }
    }

    public FileAttributes GetAttributes(string path)
    {
        if (!_nodes.TryGetValue(path, out var node))
        {
            throw new FileNotFoundException(path);
        }

        if (node.Unreadable)
        {
            throw new IOException($"'{path}' is locked.");
        }

        return node.Attributes;
    }

    public IEnumerable<string> EnumerateEntries(string path) =>
        _children.TryGetValue(path, out var list) ? list.ToArray() : [];

    public long GetFileLength(string path) => _nodes[path].Length;

    public DateTimeOffset GetLastWriteUtc(string path) => _nodes[path].LastWriteUtc;

    private void EnsureAncestors(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return;
        }

        if (!_nodes.ContainsKey(parent))
        {
            EnsureAncestors(parent);
            _nodes[parent] = new Node(FileAttributes.Directory, 0, DefaultWrite, false);
            _children[parent] = [];
            AddToParent(parent);
        }

        AddToParent(path);
    }

    private void AddToParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return;
        }

        _children.TryAdd(parent, []);
        if (!_children[parent].Contains(path))
        {
            _children[parent].Add(path);
        }
    }

    private sealed record Node(
        FileAttributes Attributes,
        long Length,
        DateTimeOffset LastWriteUtc,
        bool Unreadable);
}
