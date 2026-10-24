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

        internal sealed class Node
        {
            internal Node(Range region) : this(region, region.Offset, region.Length) { }
            internal Node(Range origin, uint offset, uint length)
            {
                Origin = origin; Region = new(offset, length, origin.IsTry);
            }
            internal Range Origin { get; }
            internal Range Region { get; private set; }
            internal void ExtendTo(uint end) => Region = new(Region.Offset, checked(end - Region.Offset), Region.IsTry);
            internal List<Node> Children { get; } = new();
        }

        internal static IReadOnlyList<Node> Build(uint methodSize, IEnumerable<Range> ranges)
        {
            var ordered = ranges.Distinct().OrderBy(r => r.Offset).ThenByDescending(r => r.Length).ThenByDescending(r => r.IsTry).ToArray();
            if (ordered.Any(range => range.End > methodSize)) throw new InvalidOperationException("EH debug range exceeds method bounds");
            var roots = new List<Node>(); var parents = new Stack<Node>();
            foreach (var range in ordered)
            {
                while (parents.Count > 0 && range.Offset >= parents.Peek().Region.End) parents.Pop();
                var node = new Node(range);
                if (parents.Count > 0)
                {
                    var parent = parents.Peek().Region;
                    if (range.End > parent.End || (range.Offset == parent.Offset && range.Length == parent.Length && range.IsTry != parent.IsTry))
                        return Partition(ordered);
                    parents.Peek().Children.Add(node);
                }
                else roots.Add(node);
                if (range.Length > 0) parents.Push(node);
            }
            return roots;
        }

        // Native optimization can share crossing/equal extents. Preserve each
        // original tag's exact PC coverage by splitting at real boundaries and
        // reusing common path prefixes. This describes native coverage, not a
        // reconstruction of C# lexical nesting. No PC or region is discarded.
        private static IReadOnlyList<Node> Partition(Range[] ordered)
        {
            const int maximumFragments = 16384;
            var boundaries = ordered.Where(r => r.Length > 0).SelectMany(r => new[] { r.Offset, checked((uint)r.End) }).Distinct().Order().ToArray();
            var roots = new List<Node>(); var path = new List<Node>(); var fragments = 0;
            for (var i = 0; i + 1 < boundaries.Length; i++)
            {
                var start = boundaries[i]; var end = boundaries[i + 1];
                var active = ordered.Where(r => r.Length > 0 && r.Offset <= start && r.End >= end).ToArray();
                var common = 0;
                while (common < path.Count && common < active.Length && path[common].Origin == active[common]) common++;
                if (common < path.Count) path.RemoveRange(common, path.Count - common);
                for (var j = 0; j < common; j++) path[j].ExtendTo(end);
                for (var j = common; j < active.Length; j++)
                {
                    if (++fragments > maximumFragments) throw new InvalidOperationException("EH debug fragmentation bound exceeded; no region omitted");
                    var node = new Node(active[j], start, end - start);
                    if (path.Count == 0) roots.Add(node); else path[^1].Children.Add(node);
                    path.Add(node);
                }
            }
            foreach (var empty in ordered.Where(r => r.Length == 0))
            {
                if (++fragments > maximumFragments) throw new InvalidOperationException("EH debug fragmentation bound exceeded; no region omitted");
                var siblings = roots;
                while (true)
                {
                    Node? container = null;
                    foreach (var candidate in siblings)
                        if (candidate.Region.Length > 0 && candidate.Region.Offset <= empty.Offset && empty.Offset < candidate.Region.End)
                        { container = candidate; break; }
                    if (container is null) break;
                    siblings = container.Children;
                }
                siblings.Add(new(empty));
                siblings.Sort((a, b) => a.Region.Offset.CompareTo(b.Region.Offset));
            }
            roots.Sort((a, b) => a.Region.Offset.CompareTo(b.Region.Offset));
            return roots;
        }
    }
// END EH PLANNER
}
