using System.Text;
using Plan9.Utf;

namespace NinePSharp.Fog.Kernel;

// Notes, as proc.c's pushnote and popnote and sysproc.c's notify: at most NNOTE wait, posting one
// interrupts a call the process is blocked in, and each is delivered as a kernel call returns.
public sealed partial class Process
{
    internal const int NoteMax = 5;
    internal const int ErrMax = 128;
    private const int ArgsMax = 128;
    private readonly object debug = new();
    private readonly List<string> notes = [];
    private Func<string, bool>? notify;
    private bool notified;
    private CancellationTokenSource interrupt = new();
    private byte[] args = [];
    private string? setArgs;

    internal long NoteId { get; set; }

    internal bool Exited { get; private set; }

    // procargs: the text and what was written to args, or each argument quoted as %q quotes it.
    internal string Args
    {
        get
        {
            lock (debug)
            {
                if (setArgs is not null)
                {
                    return $"{Text} [{setArgs}]";
                }

                return string.Join(' ', Encoding.UTF8.GetString(args).Split('\0', StringSplitOptions.None)[..^1].Select(arg => Errors.Quote(arg, int.MaxValue)));
            }
        }

        set
        {
            lock (debug)
            {
                setArgs = value;
            }
        }
    }

    /// <summary>
    /// notify(2): each note is given to the handler, which returns true to continue, as noted(NCONT)
    /// does, or false to leave the note to its default, which ends the process with it as its status.
    /// </summary>
    public void Notify(Func<string, bool>? handler)
    {
        lock (debug)
        {
            notify = handler;
        }
    }

    // pushnote: false when the process has exited or NNOTE notes already wait.
    internal bool PostNote(string note)
    {
        CancellationTokenSource interrupted;
        lock (debug)
        {
            if (Exited || notes.Count >= NoteMax)
            {
                return false;
            }

            notes.Add(note);
            interrupted = interrupt;
        }

        interrupted.Cancel();
        return true;
    }

    // devproc's read of note: the first note waiting, taken from the process.
    internal string? TakeNote()
    {
        lock (debug)
        {
            if (notes.Count == 0)
            {
                return null;
            }

            string note = notes[0];
            notes.RemoveAt(0);
            return note;
        }
    }

    // exec's copy of the arguments for proc(3): at most 128 bytes, and when that cuts the last
    // argument, a NUL at the last place fullrune finds a whole rune, as sysexec puts it.
    private void SaveArgs(IReadOnlyList<string> argv)
    {
        byte[] all = Encoding.UTF8.GetBytes(string.Concat(argv.Select(arg => arg + '\0')));
        all = all[..Math.Min(all.Length, ArgsMax)];
        if (all[^1] != 0)
        {
            int end = Enumerable.Range(1, ArgsMax - 1).Where(i => Utf.FullRune(all.AsSpan(i, ArgsMax - i))).Max();
            all[end] = 0;
            all = all[..(end + 1)];
        }

        lock (debug)
        {
            args = all;
            setArgs = null;
        }
    }

    // Each kernel call passes the process's interrupt with its own token, and fails "interrupted"
    // when a note cancels it.
    private CancellationTokenSource Interruptible(CancellationToken cancellationToken)
    {
        lock (debug)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interrupt.Token);
        }
    }

    // popnote and notify, as a call returns: the first note waiting goes to the handler, unless one
    // is being handled; with no handler, it ends the process. The interrupt is made anew, so the
    // notes still waiting are delivered as later calls return.
    private void Noted()
    {
        string note;
        Func<string, bool>? handler;
        lock (debug)
        {
            if (notes.Count == 0 || notified)
            {
                return;
            }

            note = notes[0];
            notes.RemoveAt(0);
            if (interrupt.IsCancellationRequested)
            {
                interrupt = new CancellationTokenSource();
            }

            handler = notify;
            notified = handler is not null;
        }

        if (handler is null)
        {
            throw new ExitException(note);
        }

        bool carryOn;
        try
        {
            carryOn = handler(note);
        }
        finally
        {
            lock (debug)
            {
                notified = false;
            }
        }

        if (!carryOn)
        {
            throw new ExitException(note);
        }
    }

    private void Exit()
    {
        lock (debug)
        {
            Exited = true;
            notes.Clear();
        }

        kernel.Leave(this);
    }

    // A call's end, in a using declaration: whatever the call did, a note waiting is delivered.
    private readonly struct Return(Process process) : IDisposable
    {
        public void Dispose() => process.Noted();
    }
}
