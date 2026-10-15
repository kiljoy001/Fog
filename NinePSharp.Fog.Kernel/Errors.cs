using System.Text;

namespace NinePSharp.Fog.Kernel;

internal static class Errors
{
    public const string NotExist = "file does not exist";
    public const string NotDirectory = "not a directory";
    public const string IsDirectory = "file is a directory";
    public const string NegativeOffset = "negative i/o offset";
    public const string ExecDirectory = "cannot exec directory";
    public const string BadFd = "fd out of range or not open";
    public const string NoChild = "no living children";
    public const string BadExec = "exec header invalid";
    public const string BadArg = "bad arg in system call";
    public const string BadOffset = "bad file offset or count";
    public const string BadUseFd = "inappropriate use of fd";
    public const string Permission = "permission denied";
    public const string NoFd = "no free file descriptors";
    public const string NoCreate = "mounted directory forbids creation";
    public const string Mount = "inconsistent mount";
    public const string IsMountPoint = "is a mount point";
    public const string EmptyName = "empty file name";
    public const string TooLong = "name too long";
    public const string BadChar = "bad character in file name";
    public const string BlankName = "blank file name";

    // lib9p's, for ramfs.
    public const string CreateNonDirectory = "create in non-directory";

    private const int ErrMax = 128;

    // up->genbuf, which validname's message is printed into.
    private const int GenBuf = 128;

    // needsrcquote's characters besides space and below.
    private const string RcSpecial = "`^#*[]=|\\?${}()'<>&;";

    // chan.c:validname0: a name of 64 KiB of UTF-8 or more is too long, and one holding a control
    // character, DEL or, where none is allowed, a slash has a bad character, as isfrog says. A C
    // string ends at its NUL, so a NUL in a name is a bad character too, since isfrog says so.
    public static string? NameProblem(string name, bool slashOk)
    {
        if (Encoding.UTF8.GetByteCount(name) >= 1 << 16)
        {
            return TooLong;
        }

        foreach (char c in name)
        {
            if (c < ' ' || c == '\x7f' || (c == '/' && !slashOk))
            {
                return $"{BadChar}: {Quote(name, GenBuf - 1 - BadChar.Length - 2)}";
            }
        }

        return null;
    }

    // Fog's own, which 9front does not refuse: a name, or an element of a path, of only white space.
    public static bool Blank(string name)
    {
        foreach (string element in name.Split('/'))
        {
            if (element.Length > 0 && string.IsNullOrWhiteSpace(element))
            {
                return true;
            }
        }

        return false;
    }

    // chan.c:namelenerror, showing the path up to the given number of elements.
    public static string Name(string path, int elements, string error)
    {
        if (elements == 0)
        {
            return error;
        }

        int length = End(path, elements);
        string shown = path[..length];
        if (length >= ErrMax / 3 && length + error.Length >= 2 * ErrMax / 3)
        {
            int start;
            int next = length;
            do
            {
                start = next;
                while (next > 0 && path[--next] != '/')
                {
                }
            }
            while (length - next < ErrMax / 3 || length - next + error.Length < 2 * ErrMax / 3);

            if (start == length)
            {
                start = length - (ErrMax / 4);
            }

            shown = "..." + path[start..length];
        }

        string message = $"{error}: '{shown.Replace("'", "''", StringComparison.Ordinal)}'";
        return message.Length < ErrMax ? message : message[..(ErrMax - 1)];
    }

    // fmtquote.c's %q, as much of the name as fits in room bytes, ending at a NUL, and quoted in rc's
    // way if anything of it that fits needs quoting and the quotes fit too.
    private static string Quote(string name, int room)
    {
        var shown = new StringBuilder();
        int bytes = 0;
        bool quoted = false;
        foreach (Rune c in name.EnumerateRunes())
        {
            int width = c.Utf8SequenceLength;
            if (c.Value == 0 || bytes + width > room)
            {
                break;
            }

            if (c.Value <= ' ' || (c.IsAscii && RcSpecial.Contains((char)c.Value, StringComparison.Ordinal)))
            {
                if (!quoted)
                {
                    if (1 + bytes + width + 1 > room)
                    {
                        break;
                    }

                    bytes += 2;
                    quoted = true;
                }

                if (c.Value == '\'')
                {
                    if (1 + bytes + width > room)
                    {
                        break;
                    }

                    bytes++;
                    shown.Append('\'');
                }
            }

            bytes += width;
            shown.Append(c.ToString());
        }

        return quoted ? $"'{shown}'" : shown.ToString();
    }

    private static int End(string path, int elements)
    {
        int position = 0;
        foreach (string element in path.Split('/'))
        {
            position += element.Length;
            if (element is not ("" or ".") && --elements == 0)
            {
                break;
            }

            position++;
        }

        return Math.Min(position, path.Length);
    }
}
