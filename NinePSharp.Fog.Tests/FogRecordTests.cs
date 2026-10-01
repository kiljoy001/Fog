using System.Text;
using FsCheck.Xunit;
using Xunit;

namespace NinePSharp.Fog.Tests;

public sealed class FogRecordTests
{
    internal static readonly FogRecordSchema Schema = new("fixture-v1", ["id", "value"], ["id"], ["id"]);
    private const string Header = "schema=fixture-v1\n\tcol=id\n\tcol=value\n\n";
    private const string Valid = Header + "id=one\n\tvalue=hello\n\n";

    [Theory]
    [InlineData("", 0)]
    [InlineData("id=one\n\n", 1)]
    [InlineData("id=one\n\tvalue=\n\n", 1)]
    [InlineData("id=one\n\tvalue=hello\n\nid=two\n\n", 2)]
    public void CanonicalEmptyAndNonemptyTablesRoundTrip(string body, int count)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Header + body);
        var rows = Schema.Parse(bytes, bytes.Length, Math.Max(1, count));
        Assert.Equal(count, rows.Count);
        Assert.Equal(bytes, Schema.Serialize(rows, bytes.Length, Math.Max(1, count)));
    }

    [Fact]
    public void NilEmptyAndLiteralNilRemainDistinct()
    {
        var rows = new[] { Row("absent", null), Row("empty", ""), Row("literal", "nil") };
        byte[] bytes = Schema.Serialize(rows, 4096, 3);
        string text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("id=absent\n\n", text);
        Assert.Contains("id=empty\n\tvalue=\n\n", text);
        var parsed = Schema.Parse(bytes, 4096, 3);
        Assert.Null(parsed[0]["value"]);
        Assert.Equal("", parsed[1]["value"]);
        Assert.Equal("nil", parsed[2]["value"]);
    }

    public static IEnumerable<object[]> MalformedRecords()
    {
        yield return ["\ufeff" + Valid];
        yield return [Valid.Replace("\n", "\r\n")];
        yield return [Valid[..^1]];
        yield return [Valid + "\n"];
        yield return [Header + "\n"];
        yield return [Header + "\n\n"];
        yield return [Valid.Replace("fixture-v1", "unknown")];
        yield return [Valid.Replace("\tcol=value", "\tcol=other")];
        yield return [Valid.Replace("\tcol=value", "\tcol=value\n\tcol=value")];
        yield return [Valid.Replace("value=hello", "other=hello")];
        yield return [Valid.Replace("value=hello", "value=hello\n\tvalue=again")];
        yield return [Valid + "id=one\n\tvalue=hello\n\n"];
        yield return [Valid + "id=one\n\tvalue=different\n\n"];
        yield return [Valid.Replace("value=hello", "value=nil")];
        yield return [Valid.Replace("id=one", "id=nil")];
        yield return [Valid.Replace("id=one", "id=")];
        yield return [Valid.Replace("id=one\n\tvalue=hello", "value=hello")];
        yield return [Valid.Replace("id=one\n\tvalue=hello", "value=hello\n\tid=one")];
        yield return [Valid.Replace("\tvalue=hello", "value=hello")];
        yield return [Valid.Replace("value=hello", "value=he\0llo")];
        yield return [Valid.Replace("value=hello", "value=\"hello\"")];
        yield return [Valid.Replace("value=hello", "value=\"unfinished")];
        yield return [Valid.Replace("id=one", "\tid=one")];
        yield return [Valid.Replace("id=one", "missing-equals")];
        yield return [Valid.Replace("value=hello", "value=hello extra=tuple")];
    }

    [Theory]
    [MemberData(nameof(MalformedRecords))]
    public void RejectsNoncanonicalOrAmbiguousInputBeforeUse(string text)
    {
        Assert.Equal("invalid-request", Assert.Throws<FogException>(() => Schema.Parse(Encoding.UTF8.GetBytes(text), 16384, 3)).Code);
    }

    [Fact]
    public void RejectsInvalidUtf8InsteadOfReplacingIt()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Valid);
        bytes[^3] = 0xff;
        Assert.Equal("invalid-request", Assert.Throws<FogException>(() => Schema.Parse(bytes, 4096, 2)).Code);
    }

    [Fact]
    public void PhysicalRowsAndEncodedBytesAreBoundedBeforeLibTabDeduplication()
    {
        Assert.Equal("limit", Assert.Throws<FogException>(() => Schema.Parse(Encoding.UTF8.GetBytes(Valid), Valid.Length - 1, 1)).Code);
        byte[] rows = Encoding.UTF8.GetBytes(Valid + "id=one\n\tvalue=hello\n\n");
        Assert.Equal("limit", Assert.Throws<FogException>(() => Schema.Parse(rows, 4096, 1)).Code);
        string text = Header + "id=one\n\tvalue=" + new string('x', 7160) + "\n\n";
        Assert.Single(Schema.Parse(Encoding.UTF8.GetBytes(text), 8192, 1));
        Assert.Equal("limit", Assert.Throws<FogException>(() => Schema.Parse(Encoding.UTF8.GetBytes(text.Replace("value=", "value=x")), 8192, 1)).Code);
        string unicode = Header + "id=one\n\tvalue=" + new string('é', 3581) + "\n\n";
        Assert.Equal("limit", Assert.Throws<FogException>(() => Schema.Parse(Encoding.UTF8.GetBytes(unicode), 8192, 1)).Code);
    }

    [Fact]
    public void CompositeKeysAreNotJoinedWithAnAmbiguousSeparator()
    {
        var schema = new FogRecordSchema("tuples", ["id", "value"], ["value"], ["id", "value"]);
        var rows = new[] { Row("a:b", "c"), Row("a", "b:c"), Row("a", "") };
        Assert.Equal(3, schema.Parse(schema.Serialize(rows, 4096, 3), 4096, 3).Count);
        Assert.Throws<FogException>(() => schema.Serialize([Row("a", null)], 4096, 1));
    }

    [Fact]
    public void EveryKeyFieldIsRequiredEvenWhenNotListedSeparately()
    {
        var schema = new FogRecordSchema("tuples", ["id", "value"], [], ["id", "value"]);
        byte[] missingKey = Encoding.UTF8.GetBytes("schema=tuples\n\tcol=id\n\tcol=value\n\nid=one\n\n");

        Assert.Equal("invalid-request", Assert.Throws<FogException>(() => schema.Parse(missingKey, 4096, 1)).Code);
    }

    [Fact]
    public void SchemaAndParsedRowsDoNotLendMutableBackingCollections()
    {
        string[] columns = ["id", "value"];
        string[] required = ["value"];
        string[] keys = ["id"];
        var schema = new FogRecordSchema("fixture-v1", columns, required, keys);
        columns[0] = "other";
        required[0] = "other";
        keys[0] = "other";
        var row = schema.Parse(Encoding.UTF8.GetBytes(Valid), 4096, 1)[0];
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string?>)row)["id"] = "changed");
    }

    [Fact]
    public void SerializationRejectsUnknownMissingDuplicateAndInvalidCells()
    {
        Assert.Throws<FogException>(() => Schema.Serialize([new Dictionary<string, string?> { ["id"] = "a", ["unknown"] = "x" }], 4096, 1));
        Assert.Throws<FogException>(() => Schema.Serialize([new Dictionary<string, string?>()], 4096, 1));
        Assert.Throws<FogException>(() => Schema.Serialize([Row("", "x")], 4096, 1));
        Assert.Throws<FogException>(() => Schema.Serialize([Row("one", "a"), Row("one", "b")], 4096, 2));
        Assert.Throws<FogException>(() => Schema.Serialize([Row("one", "a"), Row("one", "a")], 4096, 2));
        Assert.Throws<FogException>(() => Schema.Serialize([Row("a", "\0")], 4096, 1));
        Assert.Throws<FogException>(() => Schema.Serialize([Row("a", "\ud800")], 4096, 1));
        Assert.Throws<FogException>(() => Schema.Serialize([Row("a", new string('x', 7162))], 9000, 1));
        Assert.Equal("limit", Assert.Throws<FogException>(() => Schema.Serialize([Row("a", "a"), Row("b", "b")], 4096, 1)).Code);
        Assert.Equal("limit", Assert.Throws<FogException>(() => Schema.Serialize([Row("a", "a")], 1, 1)).Code);
        Assert.Equal("limit", Assert.Throws<FogException>(() => Schema.Serialize([], Header.Length - 1, 1)).Code);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void ParserAndWriterRequirePositiveLimits(int maxBytes, int maxRows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Schema.Parse([], maxBytes, maxRows));
        Assert.Throws<ArgumentOutOfRangeException>(() => Schema.Serialize([], maxBytes, maxRows));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad")]
    [InlineData("bad/name")]
    [InlineData("é")]
    public void InvalidSchemaNamesAreRejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new FogRecordSchema(name, ["id"], [], ["id"]));
        Assert.Throws<ArgumentException>(() => new FogRecordSchema("good", [name], [], [name]));
    }

    [Fact]
    public void SchemaValidationAndNameLengthBoundaries()
    {
        _ = new FogRecordSchema(new string('x', 128), ["a_0-b"], [], ["a_0-b"]);
        Assert.Throws<ArgumentException>(() => new FogRecordSchema(new string('x', 129), ["id"], [], ["id"]));
        Assert.Equal(
            "Invalid closed record schema.",
            Assert.Throws<ArgumentException>(() => new FogRecordSchema("good", [], [], ["id"])).Message);
        Assert.Throws<ArgumentException>(() => new FogRecordSchema("good", ["id"], [], []));
        Assert.Throws<ArgumentException>(() => new FogRecordSchema("good", ["id", "id"], [], ["id"]));
        Assert.Throws<ArgumentException>(() => new FogRecordSchema("good", ["id"], ["missing"], ["id"]));
        Assert.Throws<ArgumentException>(() => new FogRecordSchema("good", ["id"], [], ["missing"]));
        Assert.Throws<ArgumentNullException>(() => new FogRecordSchema("good", null!, [], ["id"]));
        Assert.Throws<ArgumentNullException>(() => new FogRecordSchema("good", ["id"], null!, ["id"]));
        Assert.Throws<ArgumentNullException>(() => new FogRecordSchema("good", ["id"], [], null!));
        Assert.Throws<ArgumentNullException>(() => Schema.Serialize(null!, 1, 1));
    }

    [Property(MaxTest = 200)]
    public bool TextRoundTripsWithoutReimplementingLibTabEscaping(byte[] data)
    {
        string text = "# nil = \t\n\r\" & < é " + Convert.ToBase64String(data.Take(512).ToArray());
        byte[] bytes = Schema.Serialize([Row("a", text)], 7168, 1);
        return Schema.Parse(bytes, 7168, 1)[0]["value"] == text;
    }

    [Property(MaxTest = 300)]
    public bool ArbitraryBytesEitherRejectOrHaveAnExactCanonicalRoundTrip(byte[] data)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows;
        try
        {
            rows = Schema.Parse(data, 16384, 16);
        }
        catch (FogException)
        {
            return true;
        }

        return data.SequenceEqual(Schema.Serialize(rows, 16384, 16));
    }

    internal static IReadOnlyDictionary<string, string?> Row(string id, string? value) =>
        new Dictionary<string, string?> { ["id"] = id, ["value"] = value };
}
