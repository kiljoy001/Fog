using NinePSharp.Fog.Gefs.Tests.Support;
using Reqnroll;
using Xunit;

namespace NinePSharp.Fog.Gefs.Tests.Steps;

[Binding]
public sealed class OracleSteps
{
    private readonly MemoryStore store = new();
    private readonly List<string> differences = [];
    private Tree? tree;

    [Given("a tree whose root is a leaf holding only the key 10")]
    public void GivenSentinel()
    {
        Blk leaf = store.New(BlockType.Leaf);
        leaf.SetValue([0x10], []);
        store.Enqueue(leaf);
        tree = new Tree(store, leaf.Pointer, 1);
    }

    [When(@"^the upserts of the ([a-z-]+) oracle run are applied$")]
    public void WhenOracleRun(string run)
    {
        Oracle oracle = Oracle.Load(run);
        var expected = oracle.Expected.ToDictionary(e => e.Step);
        foreach (var (step, batch) in oracle.Upserts())
        {
            tree!.Upsert(batch);
            if (expected.TryGetValue(step, out var shape))
            {
                var (_, height, digest) = shape;
                ulong actual = Oracle.Digest(store, tree);
                if ((height, digest) != (tree.Height, actual))
                {
                    differences.Add($"after upsert {step}: gefs had {height} high, {digest:x16}; the port has {tree.Height} high, {actual:x16}");
                }
            }
        }
    }

    [Then("after every tenth the tree has the shape gefs's own tree.c gave it")]
    public void ThenSameShape() => Assert.Empty(differences);
}
