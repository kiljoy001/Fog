using System.Globalization;
using Xunit;

namespace NinePSharp.Fog.Wasm.Tests;

public sealed class WasmBinaryTests
{
    [Theory]
    [InlineData("00", 0u)]
    [InlineData("7f", 127u)]
    [InlineData("8a 01", 138u)]
    [InlineData("e5 8e 26", 624485u)]
    [InlineData("ff ff ff ff 0f", uint.MaxValue)]
    public void Leb128ReadsTheNumberAndLeavesTheOffsetPastIt(string bytes, uint value)
    {
        byte[] image = [0xaa, .. bytes.Split(' ').Select(b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), 0xaa];
        int offset = 1;

        Assert.Equal(value, WasmBinary.Leb128(image, ref offset));
        Assert.Equal(image.Length - 1, offset);
    }
}
