namespace NinePSharp.Fog.Wasm;

internal static class WasmBinary
{
    // An unsigned LEB128 number at offset, which is left just past it.
    public static uint Leb128(byte[] image, ref int offset)
    {
        uint value = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = image[offset++];
            value += (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }
    }
}
