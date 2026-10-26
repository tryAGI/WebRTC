using System.Text.Json.Nodes;
using ILCompiler.ObjectWriter;
using R = ILCompiler.ObjectWriter.EhRangePlanner.Range;

var ranges = new R[] { new(0, 48, true), new(8, 16, true), new(24, 8, false), new(48, 16, false), new(8, 16, true) };
var tree = EhRangePlanner.Build(64, ranges.Reverse());
Check(tree.Count == 2 && tree[0].Children.Count == 2, "Nested/shared try grouping");
Check(Count(tree) == ranges.Distinct().Count(), "No distinct native region lost");
Check(EhRangePlanner.Build(64, [new(0, 8, true), new(8, 8, false), new(16, 0, true)]).Count == 3, "Touching/empty regions");
var reports = new JsonArray();
foreach (var scenario in new R[][]
{
    ranges,
    [new(0, 24, true), new(8, 24, false), new(16, 0, true), new(64, 0, false)],
    [new(0, 24, true), new(0, 24, false)],
    [new(0, 48, true), new(8, 32, true), new(24, 24, false), new(48, 16, false)]
})
{
    var nodes = EhRangePlanner.Build(64, scenario.Reverse());
    var flat = Flatten(nodes).ToArray();
    foreach (var origin in scenario.Distinct().Where(r => r.Length == 0))
        Check(flat.Count(n => n.Origin == origin) == 1, "Empty native scope lost or duplicated");
    foreach (var origin in scenario.Distinct())
        for (uint pc = 0; pc < 64; pc++)
        {
            var expected = origin.Offset <= pc && pc < origin.End;
            var count = flat.Count(n => n.Origin == origin && n.Region.Offset <= pc && pc < n.Region.End);
            Check(count == (expected ? 1 : 0), "Native scope PC coverage lost or duplicated");
        }
    VerifyChildren(nodes, 0, 64);
    JsonNode item = new JsonObject { ["methodSize"] = 64, ["distinctRegions"] = scenario.Distinct().Count(), ["roots"] = Nodes(nodes) };
    reports.Add(item);
}
foreach (var bad in new R[][] { [new(uint.MaxValue, 2, true)], [new(0, 65, true)] })
{
    var rejected = false;
    try { EhRangePlanner.Build(64, bad); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Out-of-bounds/overflow range must fail closed");
}
var bounded = false;
try { EhRangePlanner.Build(2000, Enumerable.Range(0, 300).Select(i => new R((uint)i, 1000, true))); }
catch (InvalidOperationException) { bounded = true; }
Check(bounded, "Hostile fragmentation must stay bounded");
Console.WriteLine(new JsonObject { ["cases"] = reports }.ToJsonString());

static IEnumerable<EhRangePlanner.Node> Flatten(IReadOnlyList<EhRangePlanner.Node> nodes)
{
    foreach (var n in nodes) { yield return n; foreach (var c in Flatten(n.Children)) yield return c; }
}
static void VerifyChildren(IReadOnlyList<EhRangePlanner.Node> nodes, uint start, ulong end)
{
    ulong previousEnd = start;
    foreach (var node in nodes)
    {
        Check(node.Region.Offset >= previousEnd && node.Region.End <= end, "Sibling overlap or parent bounds invalid");
        Check(node.Region.Offset >= node.Origin.Offset && node.Region.End <= node.Origin.End, "Fragment exceeds original scope");
        VerifyChildren(node.Children, node.Region.Offset, node.Region.End);
        previousEnd = node.Region.End;
    }
}

static void Check(bool condition, string reason) { if (!condition) throw new IOException(reason); }
static int Count(IReadOnlyList<EhRangePlanner.Node> nodes) => nodes.Sum(n => 1 + Count(n.Children));
static JsonArray Nodes(IReadOnlyList<EhRangePlanner.Node> nodes)
{
    var result = new JsonArray();
    foreach (var node in nodes)
    {
        JsonNode item = new JsonObject { ["offset"] = node.Region.Offset, ["length"] = node.Region.Length,
            ["try"] = node.Region.IsTry, ["originOffset"] = node.Origin.Offset, ["originLength"] = node.Origin.Length, ["children"] = Nodes(node.Children) };
        result.Add(item);
    }
    return result;
}
