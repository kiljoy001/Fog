using System.Text;

namespace NinePSharp.Fog.Rc;

// rc's strings hold bytes, one per char; the kernel's names and arguments are UTF-8 text.
internal static class RcText
{
    public static byte[] Bytes(string bytes) => Encoding.Latin1.GetBytes(bytes);

    public static string String(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    public static string Kernel(string bytes) => Encoding.UTF8.GetString(Bytes(bytes));

    public static string Rc(string text) => String(Encoding.UTF8.GetBytes(text));
}
