using System.Buffers.Binary;
using System.Text;
using Dp9ik;
using Xunit;

namespace NinePSharp.Fog.Auth.Tests;

/// <summary>The record layout of 9front's AES keyfile, and the checks on records a database is loaded from.</summary>
public sealed class KeyDatabaseTests
{
    [Fact]
    public void Records_Round_Trip_Every_Persisted_Field()
    {
        var database = new KeyDatabase();
        KeyUser user = database.Add("glenda");
        AuthKey key = AuthKey.FromPassword("glenda-password");
        user.DesKey = key.DesKey;
        user.AesKey = key.AesKey;
        user.Secret = Encoding.UTF8.GetBytes(new string('s', KeyDatabase.SecretLength - 1));
        user.Disabled = true;
        user.Warnings = 3;
        user.Expire = 4102444800;
        user.Bad = 9;

        byte[] records = database.Encode();
        KeyUser loaded = Assert.Single(KeyDatabase.Decode(records).Users);

        Assert.Equal(KeyDatabase.RecordLength, records.Length);
        Assert.Equal(89, KeyDatabase.RecordLength);
        Assert.Equal("glenda", loaded.Name);
        Assert.Equal(key.DesKey, loaded.DesKey);
        Assert.Equal(key.AesKey, loaded.AesKey);
        Assert.Equal(user.Secret, loaded.Secret);
        Assert.True(loaded.Disabled);
        Assert.Equal(3, loaded.Warnings);
        Assert.Equal(4102444800u, loaded.Expire);
        Assert.Equal(0UL, loaded.Bad);
        key.ApplyAuthPakHash("glenda");
        Assert.Equal(key.PakHash, loaded.PakHash);
    }

    [Fact]
    public void Records_Use_The_9front_Field_Offsets()
    {
        var database = new KeyDatabase();
        KeyUser user = database.Add("ab");
        user.DesKey = [1, 2, 3, 4, 5, 6, 7];
        user.Disabled = true;
        user.Warnings = 9;
        user.Expire = 0x01020304;
        user.Secret = "xy"u8.ToArray();
        user.AesKey = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();
        byte[] record = database.Encode();

        Assert.Equal("ab\0"u8.ToArray(), record[..3]);
        Assert.Equal(user.DesKey, record[28..35]);
        Assert.Equal(1, record[35]);
        Assert.Equal(9, record[36]);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(37)));
        Assert.Equal("xy\0"u8.ToArray(), record[41..44]);
        Assert.Equal(user.AesKey, record[73..89]);
    }

    [Fact]
    public void Users_Are_Encoded_And_Listed_In_The_Order_They_Were_Made()
    {
        var database = new KeyDatabase();
        foreach (string name in new[] { "zed", "amy", "mid" }) database.Add(name);
        Assert.Equal(["zed", "amy", "mid"], database.Users.Select(user => user.Name));
        Assert.Equal(["zed", "amy", "mid"], KeyDatabase.Decode(database.Encode()).Users.Select(user => user.Name));
        Assert.Equal(3, database.Users.Select(user => user.Uniq).Distinct().Count());
    }

    [Fact]
    public void A_Secret_Filling_Its_Field_Is_Cut_At_31_Bytes_As_keyfs_Terminates_It()
    {
        byte[] record = Record("glenda");
        record.AsSpan(41, KeyDatabase.SecretLength).Fill((byte)'s');
        Assert.Equal(new string('s', 31), Encoding.UTF8.GetString(Assert.Single(KeyDatabase.Decode(record).Users).Secret));
    }

    [Fact]
    public void An_Empty_Secret_Round_Trips_As_Empty()
    {
        var database = new KeyDatabase();
        database.Add("glenda");
        Assert.Empty(Assert.Single(KeyDatabase.Decode(database.Encode()).Users).Secret);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(88)]
    [InlineData(90)]
    public void A_Partial_Record_Is_Refused(int length)
        => Assert.Equal("The database holds a partial record.",
            Assert.Throws<InvalidDataException>(() => KeyDatabase.Decode(new byte[length])).Message);

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData(".")]
    public void A_Record_With_An_Invalid_Name_Is_Refused(string name)
        => Assert.Equal("The database holds an invalid user name.",
            Assert.Throws<InvalidDataException>(() => KeyDatabase.Decode(Record(name))).Message);

    [Fact]
    public void A_Name_Without_A_Terminator_Is_Refused()
    {
        byte[] record = Record("glenda");
        record.AsSpan(0, KeyDatabase.NameLength).Fill((byte)'n');
        Assert.Throws<InvalidDataException>(() => KeyDatabase.Decode(record));
    }

    [Fact]
    public void Duplicate_Users_Are_Refused()
        => Assert.Throws<InvalidDataException>(() => KeyDatabase.Decode([.. Record("glenda"), .. Record("glenda")]));

    [Theory]
    [InlineData("glenda", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz01", false)]
    [InlineData("ユーザー", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("...", true)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("a\tb", false)]
    [InlineData("a\u007fb", false)]
    [InlineData("a�b", false)]
    [InlineData("a\ud800b", false)]
    public void Names_Follow_keyfs_userok(string name, bool valid) => Assert.Equal(valid, KeyDatabase.IsValidName(name));

    [Fact]
    public void A_Copy_Is_Independent_And_Keeps_The_Counters_Held_In_Memory()
    {
        var database = new KeyDatabase();
        KeyUser user = database.Add("glenda");
        user.Bad = 4;
        user.PurgatoryEnds = 77;
        KeyDatabase copy = database.Copy();
        KeyUser copied = copy.FindByUniq(user.Uniq)!;
        copied.Warnings = 5;
        copied.DesKey[0] = 0xFF;
        copy.Add("scott");

        Assert.Equal(4UL, copied.Bad);
        Assert.Equal(77, copied.PurgatoryEnds);
        Assert.Equal(0, user.Warnings);
        Assert.Equal(0, user.DesKey[0]);
        Assert.Null(database.Find("scott"));
        Assert.NotEqual(user.Uniq, copy.Find("scott")!.Uniq);
        Assert.Null(database.FindByUniq(999));
    }

    private static byte[] Record(string name)
    {
        var record = new byte[KeyDatabase.RecordLength];
        Encoding.UTF8.GetBytes(name).CopyTo(record, 0);
        return record;
    }
}
