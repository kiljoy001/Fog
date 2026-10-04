using System.Globalization;
using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Rc;

// havefork.c: the operations that fork. The child's half of each runs in the shell's copy in the
// new process, before it goes on running code. The kernel's fork does not fail for rc's flags, as
// 9front's waits for a free process, so rc's "try again" cannot happen.
internal sealed partial class RcShell
{
    private const int Prd = 0;
    private const int Pwr = 1;

    // Fork: the environment is written out first, so the child's commands see it.
    private async ValueTask<long> ForkAsync(Func<RcShell, ValueTask> child)
    {
        await UpdenvAsync();
        RcShell copy = Clone();
        long pid = process.Fork(RforkFlags.Fdg | RforkFlags.Rend, async forked =>
        {
            copy.process = forked;
            await child(copy);
            await copy.RunAsync();
        });
        waitpids.Add(pid);
        return pid;
    }

    private async ValueTask XasyncAsync()
    {
        RcThread p = runq!;
        long pid = await ForkAsync(child =>
        {
            child.Start(child.runq!.Code, child.runq.Pc + 1, child.runq.Local, child.runq.Redir);
            child.runq!.Ret = null;
            return ValueTask.CompletedTask;
        });

        p.Pc = p.Code[p.Pc].I;
        Setvar("apid", new RcWord(pid.ToString(CultureInfo.InvariantCulture), null));
    }

    private async ValueTask XpipeAsync()
    {
        RcThread p = runq!;
        int pc = p.Pc;
        int lfd = p.Code[pc++].I;
        int rfd = p.Code[pc++].I;
        int[] pfd = await PipeAsync();
        if (pfd.Length == 0)
        {
            await Xerror2Async("can't get pipe", errstr);
            return;
        }

        long pid = await ForkAsync(async child =>
        {
            await child.CloseAsync(pfd[Prd]);
            child.Start(child.runq!.Code, pc + 2, child.runq.Local, child.runq.Redir);
            child.runq!.Ret = null;
            await child.PushredirAsync(RcRedir.Open, pfd[Pwr], lfd);
        });

        await CloseAsync(pfd[Pwr]);
        Start(p.Code, p.Code[pc].I, runq!.Local, runq.Redir);
        await PushredirAsync(RcRedir.Open, pfd[Prd], rfd);
        p.Pc = p.Code[pc + 1].I;
        p.Pid = pid;
    }

    // Xbackq: the output of {} split at $ifs, or at the bytes given, pushed as words.
    private async ValueTask XbackqAsync()
    {
        int[] pfd = await PipeAsync();
        if (pfd.Length == 0)
        {
            await Xerror2Async("can't make pipe", errstr);
            return;
        }

        long pid = await ForkAsync(async child =>
        {
            await child.CloseAsync(pfd[Prd]);
            child.Start(child.runq!.Code, child.runq.Pc + 1, child.runq.Local, child.runq.Redir);
            await child.PushredirAsync(RcRedir.Open, pfd[Pwr], 1);
        });

        await CloseAsync(pfd[Pwr]);
        string split = Popword();
        Poplist();
        RcIo f = RcIo.OpenFd(pfd[Prd]);
        RcWord? end = runq!.Argv!.Words;
        var words = new List<string>();
        for (string? s; (s = await f.RstrAsync(process, split)) is not null;)
        {
            words.Add(s);
        }

        RcWord? list = end;
        for (int i = words.Count - 1; i >= 0; i--)
        {
            list = new RcWord(words[i], list);
        }

        runq.Argv.Words = list;
        await f.CloseAsync(process);
        await WaitforAsync(pid);
        runq.Pc = runq.Code[runq.Pc].I;
    }

    // Xpipefd: {} on a pipe, its other end pushed as /fd/n.
    private async ValueTask XpipefdAsync()
    {
        RcThread p = runq!;
        int pc = p.Pc;
        bool read = p.Code[pc].I == RcToken.Read;
        int[] pfd = await PipeAsync();
        if (pfd.Length == 0)
        {
            await Xerror2Async("can't get pipe", errstr);
            return;
        }

        int sidefd = read ? pfd[Pwr] : pfd[Prd];
        int mainfd = read ? pfd[Prd] : pfd[Pwr];
        await ForkAsync(async child =>
        {
            await child.CloseAsync(mainfd);
            child.Start(child.runq!.Code, pc + 2, child.runq.Local, child.runq.Redir);
            await child.PushredirAsync(RcRedir.Open, sidefd, read ? 1 : 0);
            child.runq!.Ret = null;
        });

        await CloseAsync(sidefd);
        await PushredirAsync(RcRedir.Open, mainfd, mainfd);
        Shuffleredir();
        Pushword(Fdprefix + mainfd.ToString(CultureInfo.InvariantCulture));
        p.Pc = p.Code[pc + 1].I;
    }

    private async ValueTask XsubshellAsync()
    {
        long pid = await ForkAsync(child =>
        {
            child.Start(child.runq!.Code, child.runq.Pc + 1, child.runq.Local, child.runq.Redir);
            child.runq!.Ret = null;
            return ValueTask.CompletedTask;
        });

        await WaitforAsync(pid);
        runq!.Pc = runq.Code[runq.Pc].I;
    }

    // execforkexec: the child runs exec with the command's arguments.
    private ValueTask<long> ExecforkexecAsync() => ForkAsync(child => child.ExecAsync());
}
