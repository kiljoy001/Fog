namespace NinePSharp.Fog.Kernel;

internal static class Errors
{
    public const string NotExist = "file does not exist";
    public const string NotDirectory = "not a directory";
    public const string ExecDirectory = "cannot exec directory";
    public const string BadFd = "fd out of range or not open";
    public const string NoChild = "no living children";
    public const string BadExec = "exec header invalid";
    public const string BadArg = "bad arg in system call";

    private const int ErrMax = 128;

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
