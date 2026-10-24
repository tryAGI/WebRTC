using System;
using System.Collections.Generic;
using System.Linq;

namespace ILCompiler.ObjectWriter
{
// BEGIN EH PLANNER
    // Authored experimental compiler helper, MIT. Exact duplicate native ranges
    // carry no additional metadata in the original emitter. Preserve every
    // distinct region; nest containment rather than overlapping siblings.
    internal static class EhRangePlanner
    {
        internal readonly record struct Range(uint Offset, uint Length, bool IsTry)
        {
            internal ulong End => (ulong)Offset + Length;
        }

        internal sealed class Node(Range range)
        {
            internal Range Region { get; } = range;
            internal List<Node> Children { get; } = new();
        }

        internal static IReadOnlyList<Node> Build(uint methodSize, IEnumerable<Range> ranges)
        {
            var roots = new List<Node>(); var parents = new Stack<Node>();
            foreach (var range in ranges.Distinct().OrderBy(r => r.Offset).ThenByDescending(r => r.Length).ThenByDescending(r => r.IsTry))
            {
                if (range.End > methodSize) throw new InvalidOperationException("EH debug range exceeds method bounds");
                while (parents.Count > 0 && range.Offset >= parents.Peek().Region.End) parents.Pop();
                var node = new Node(range);
                if (parents.Count > 0)
                {
                    var parent = parents.Peek().Region;
                    if (range.End > parent.End || (range.Offset == parent.Offset && range.Length == parent.Length && range.IsTry != parent.IsTry))
                        throw new InvalidOperationException("EH native ranges require a non-laminar representation");
                    parents.Peek().Children.Add(node);
                }
                else roots.Add(node);
                if (range.Length > 0) parents.Push(node);
            }
            return roots;
        }
    }
// END EH PLANNER
}
