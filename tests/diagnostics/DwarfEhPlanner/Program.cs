using System.Text.Json.Nodes;
using ILCompiler.ObjectWriter;
using R = ILCompiler.ObjectWriter.EhRangePlanner.Range;

var ranges = new R[] { new(0, 48, true), new(8, 16, true), new(24, 8, false), new(48, 16, false), new(8, 16, true) };
var tree = EhRangePlanner.Build(64, ranges.Reverse());
Check(tree.Count == 2 && tree[0].Children.Count == 2, "Nested/shared try grouping");
Check(Count(tree) == ranges.Distinct().Count(), "No distinct native region lost");
Check(EhRangePlanner.Build(64, [new(0, 8, true), new(8, 8, false), new(16, 0, true)]).Count == 3, "Touching/empty regions");
foreach (var bad in new R[][] { [new(0, 16, true), new(8, 16, false)], [new(uint.MaxValue, 2, true)], [new(0, 65, true)], [new(0, 8, true), new(0, 8, false)] })
{
    var rejected = false;
    try { EhRangePlanner.Build(64, bad); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Unsupported/crossing/out-of-bounds range must fail closed");
}
var report = new JsonObject { ["methodSize"] = 64, ["distinctRegions"] = Count(tree), ["roots"] = Nodes(tree) };
Console.WriteLine(report.ToJsonString());

static void Check(bool condition, string reason) { if (!condition) throw new IOException(reason); }
static int Count(IReadOnlyList<EhRangePlanner.Node> nodes) => nodes.Sum(n => 1 + Count(n.Children));
static JsonArray Nodes(IReadOnlyList<EhRangePlanner.Node> nodes)
{
    var result = new JsonArray();
    foreach (var node in nodes)
    {
        JsonNode item = new JsonObject { ["offset"] = node.Region.Offset, ["length"] = node.Region.Length,
            ["try"] = node.Region.IsTry, ["children"] = Nodes(node.Children) };
        result.Add(item);
    }
    return result;
}
