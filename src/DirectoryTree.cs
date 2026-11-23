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
        }

        public void Sort(int column, bool descending)
        {
            Comparison<Node> compare = (left, right) =>
            {
                if (column == 0)
                    return descending ? CompareName(right, left) : CompareName(left, right);
                long? first = Size(left.Change, column), second = Size(right.Change, column);
                if (first.HasValue != second.HasValue) return first.HasValue ? -1 : 1;
                int order = descending ? Nullable.Compare(second, first) : Nullable.Compare(first, second);
                return order == 0 ? CompareName(left, right) : order;
            };
            foreach (Node node in nodes.Values)
                if (node.Children.Count > 1) node.Children.Sort(compare);
        }

        private static int CompareName(Node left, Node right)
        {
            int order = StringComparer.CurrentCultureIgnoreCase.Compare(left.Caption, right.Caption);
            return order == 0 ? StringComparer.Ordinal.Compare(left.Change.Path, right.Change.Path) : order;
        }

        private static long? Size(DirectoryChange change, int column)
        {
            return column == 1 ? change.Before : column == 2 ? change.After : change.Delta;
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
