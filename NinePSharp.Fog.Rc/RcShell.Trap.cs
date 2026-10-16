namespace NinePSharp.Fog.Rc;

// trap.c, and plan9.c's notes: notifyf counts each note rc traps, and dotrap runs what each asks
// for once the instruction the note came in has run.
internal sealed partial class RcShell
{
    private const int Sigint = 2;
    private const int Sigquit = 3;

    // Signame and syssigname: the function each trap runs, and the start of the note it traps.
    private static readonly string[] Signame = ["sigexit", "sighup", "sigint", "sigquit", "sigalrm", "sigkill", "sigfpe", "sigterm"];
    private static readonly string[] Syssigname = ["exit", "hangup", "interrupt", "quit", "alarm", "kill", "sys: fp: ", "term"];

    private int ntrap;
    private int[] trap = new int[Signame.Length];
    private bool interrupted;

    // Trapinit
    private void Trapinit() => process.Notify(Notifyf);

    // notifyf: a note rc traps is counted and rc goes on, a second interrupt while one waits being
    // dropped; any other is left to its default.
    private bool Notifyf(string s)
    {
        for (int i = 0; i < Syssigname.Length; i++)
        {
            if (s.StartsWith(Syssigname[i], StringComparison.Ordinal))
            {
                if (!s.StartsWith("sys: ", StringComparison.Ordinal))
                {
                    interrupted = true;
                }

                if (s != "interrupt" || trap[i] == 0)
                {
                    trap[i]++;
                    ntrap++;
                }

                return true;
            }
        }

        return false;
    }

    // Eintr: whether a note interrupted rc since Noerror.
    private bool Eintr() => interrupted;

    private void Noerror() => interrupted = false;

    // dotrap: each trap counted runs the function named for it with the shell's $*. An interrupt or
    // quit with none runs the stack down to the thread reading commands, and Xreturn ends a shell
    // that has none; any other with none ends the shell. 9front's sigwinch case is left out, as no
    // note is trapped as sigwinch.
    private async ValueTask DotrapAsync()
    {
        RcWord? starval = Vlook("*").Val;
        while (ntrap != 0)
        {
            for (int i = 0; i < Signame.Length; i++)
            {
                while (trap[i] != 0)
                {
                    --trap[i];
                    --ntrap;
                    if (process.Pid != mypid)
                    {
                        await ExitAsync();
                    }

                    RcVar trapreq = Vlook(Signame[i]);
                    if (trapreq.Fn is not null)
                    {
                        Startfunc(trapreq, RcWord.Copy(starval, null), null, null);
                    }
                    else if (i is Sigint or Sigquit)
                    {
                        while (!runq!.IFlag)
                        {
                            await XreturnAsync();
                        }
                    }
                    else
                    {
                        await ExitAsync();
                    }
                }
            }
        }
    }

    // flushio: a write that failed runs the traps a note left waiting, if any.
    private async ValueTask FlushioAsync(RcIo f)
    {
        if (!await f.FlushAsync(process))
        {
            await DotrapAsync();
        }
    }
}
