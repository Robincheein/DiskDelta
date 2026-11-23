using System;
using System.Collections.Generic;
using System.Linq;

namespace DiskDelta
{
    internal sealed class DirectoryTree
    {
        internal sealed class Node
        {
            public DirectoryChange Change;
            public readonly List<Node> Children = new List<Node>();
            public Node Parent;
            public int Depth;
            public bool Expanded;

            public string Caption
            {
                get
                {
                    string path = Change.Path.TrimEnd('\\');
                    return path.Substring(path.LastIndexOf('\\') + 1);
                }
            }
        }

        private readonly string root;
        private readonly Dictionary<string, Node> nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        public Node Root { get; private set; }

        public DirectoryTree(string root, IEnumerable<DirectoryChange> changes)
        {
            this.root = root;
            foreach (DirectoryChange change in changes)
            {
                if (change.Path.StartsWith(root, StringComparison.Ordinal) && change.Path.EndsWith("\\", StringComparison.Ordinal))
                    Link(change.Path).Change = change;
            }
            if (Root != null) Root.Expanded = true;
            foreach (Node node in nodes.Values)
                if (node.Children.Count > 1)
                    node.Children.Sort((left, right) =>
                    {
                        int order = Math.Abs(right.Change.Delta ?? 0).CompareTo(Math.Abs(left.Change.Delta ?? 0));
                        return order == 0 ? StringComparer.CurrentCultureIgnoreCase.Compare(left.Caption, right.Caption) : order;
                    });
        }

        public static DirectoryTree FromLegacy(Report report)
        {
            return new DirectoryTree(report.Root, (report.FolderChanges ?? Enumerable.Empty<Change>()).Select(item => new DirectoryChange
            {
                Path = item.Path.TrimEnd('\\') + "\\", Delta = item.Delta
            }));
        }

        private Node Link(string path)
        {
            Node node;
            if (nodes.TryGetValue(path, out node)) return node;
            node = new Node { Change = new DirectoryChange { Path = path } };
            nodes.Add(path, node);
            if (path == root) Root = node;
            else
            {
                string parent = path.Substring(0, path.LastIndexOf('\\', path.Length - 2) + 1);
                node.Parent = Link(parent);
                node.Depth = node.Parent.Depth + 1;
                node.Parent.Children.Add(node);
            }
            return node;
        }

        public void Visible(List<Node> rows)
        {
            rows.Clear();
            if (Root != null) Append(rows, Root);
        }

        private static void Append(List<Node> rows, Node node)
        {
            rows.Add(node);
            if (node.Expanded)
                foreach (Node child in node.Children) Append(rows, child);
        }
    }
}
