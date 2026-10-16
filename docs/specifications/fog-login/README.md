# fog-login: rc on Fog from any shell

Status: design; slices 1 to 3, the host booting the kernel on gefs, the mount driver, and notes with /proc and rc's traps, are built.

A user reaches Fog the way they reach a 9front CPU server: they get rc, running on Fog as a
process like any other, with their own machine's files at `/mnt/term`. Everything they do there
is rc and files: installing an application is `cp`, starting it is running it, and looking at it
is reading `/mnt/{app}`. Fog adds no commands of its own for logging in; the commands that manage
applications are rc scripts in Fog's `/bin`, the same for every client.

Three clients reach the same listener:

- stock 9front `rcpu`, unchanged;
- drawterm, unchanged;
- `fog`, a .NET rcpu client for PowerShell, bash and any other shell on Linux, macOS and
  Windows. It takes `rcpu`'s arguments.

## Sources

| What | 9front `origin/front` |
| --- | --- |
| The rcpu client and the script it sends | `rc/bin/rcpu`, `rc/bin/rconnect` |
| The listener's script | `rc/bin/service/tcp17019` |
| The network device and its users | `sys/src/9/ip/devip.c`, `sys/man/3/ip`, `sys/src/libc/9sys/dial.c`, `announce.c` |
| Listening and naming | `sys/src/cmd/aux/listen.c`, `sys/man/8/listen`, `sys/src/cmd/ndb/cs.c`, `rc/bin/cpurc` |
| `/net` over host sockets | drawterm `kern/devip.c`, `kern/devip-posix.c`, `kern/devip-win32.c` |
| Authentication and TLS | `sys/src/cmd/tlssrv.c`, `sys/src/cmd/tlsclient.c`, `sys/src/libsec/port/tlshand.c` |
| The authentication agent | `sys/src/cmd/auth/factotum`, `sys/src/libauth` (`auth_proxy`, `auth_chuid`), `sys/man/4/factotum` |
| The secure store | `sys/src/cmd/auth/secstore` (`secstored.c`, `secstore.c`, `pak.c`), `sys/man/1/secstore`, `sys/man/8/secstore` |
| Capabilities and `/srv` | `sys/src/9/port/devcap.c`, `sys/src/9/port/devsrv.c` |
| The root at boot, and a new user's profile | `sys/src/9/boot/bootrc`, `sys/lib/newuser` |
| Mounting a 9P conversation | `sys/src/9/port/devmnt.c`, `sys/src/9/port/sysfile.c` (`bindmount`) |
| Notes and `/proc` | `sys/src/9/port/proc.c` (`postnote`, `postnotepg`, `pushnote`, `popnote`), `sys/src/9/port/devproc.c` |
| rc's traps | `sys/src/cmd/rc/plan9.c` (`notifyf`, `Trapinit`, `Waitfor`) |
| Commands the scripts use | `sys/src/cmd/mount.c`, `rm.c`, `test.c`, `read.c` |
| A non-Plan 9 rcpu client | drawterm `cpu.c` (`rcpu`, `rcpuexit`), `kern/devfs-posix.c`, `kern/devfs-win32.c` |

The TLS is `Dp9ik.Tls` from dp9ik.net: TLS 1.2 PSK with identity `p9secret` and libsec's three
PSK suites, keyed by the dp9ik session secret.

## What a user does

```text
PS C:\work> fog -h fog.example
password:
term% cp app.wasm /bin/app
term% app &
term% cat /mnt/app/status
term% ^D
PS C:\work>
```

```sh
fog -h fog.example -c 'ls /bin'
cat notes.txt | fog -h fog.example -c 'wc -l'
```

`fog [-u user] [-h host] [-c cmd arg ...]` is `rcpu`'s usage, without `-k`, `-P`, `-p` and `-t`.
`$cpu` names the host when `-h` does not, and the user is the local login name when `-u` does not
give one. With no `-c`, rc is interactive (`rc -li`); with `-c`, rc runs the command
(`rc -lc`) with the local standard input, output and error as its own. `fog` exits 0 when rc's
status is empty and 1 otherwise, as an rcpu client in a Unix shell can only say whether the
status was empty.

rc starts in the local working directory, seen through `/mnt/term`. The local file system is
`/mnt/term/root`, as drawterm puts it: `/` on Linux and macOS, and on Windows a directory of
the drives, named as drawterm names them (`C:`).

## The listener

Fog's host boots the kernel on gefs and runs the boot as 9front's `bootrc` and `cpurc` do: the
host owner's factotum, `#I` bound on `/net`, `ndb/cs`, `auth/secstored`, and
`aux/listen -q -d /rc/bin/service tcp`. From then on the listener is 9front's. `aux/listen`
announces `tcp!*!17019` for `tcp17019`, becomes `none`, and for each call starts the script
with the call as its descriptors 0 and 1. The script runs `tlssrv -a /bin/rc -c server`, which
does the rest as it does on 9front:

1. `auth_proxy` with `proto=p9any role=server` through the host owner's factotum at
   `/mnt/factotum/rpc`, which serves the server role for any process, `none` included.
   Factotum offers dp9ik with the host owner's key; the call is refused if this fails.
2. `auth_chuid`: the capability factotum made for the user is written to `/dev/capuse`, the
   process becomes the user, mounts `/srv/factotum` on `/mnt` and builds the user's namespace.
3. TLS-PSK as the server, keyed by the session secret.
4. `/bin/rc -c server` with the `server` function of `tcp17019`: read a length line, then that
   many bytes, and run them as rc.

The script the client sends is the client's. Stock rcpu sends rcpu's `server` function; drawterm
sends its own; `fog` sends rcpu's. Each script mounts the conversation with `mount -nc /fd/0
/mnt/term`, binds the client's console, and runs rc as the user. rcpu's script also forwards the
client's interrupts from `/mnt/term/mnt/cpunote/data` to the session's note group, and writes
rc's status to `/mnt/term/env/rstatus`.

## Factotum and secstore

Factotum and secstore are ported from 9front, and run as kernel processes like any other
program. As on a 9front CPU server:

- At boot the host starts `auth/factotum -n -sfactotum -S` as the host owner. In place of
  `readnvram`, `-S` reads the host owner's dp9ik key from the host's sealed key store, the key
  keyfs already holds for the domain. Factotum posts `/srv/factotum`, and its `ctl`, `rpc`,
  `proto`, `needkey`, `confirm` and `log` are as `factotum(4)` gives them.
- Factotum makes a capability for the user it authenticates by writing to `/dev/caphash`, as
  `devcap` takes it, and `auth_chuid` spends it at `/dev/capuse`. Only the host owner may write
  `caphash`, and a capability is used once.
- `auth/secstored` runs as the host owner as the domain's secure store. It announces
  `tcp!*!5356` itself, as 9front's does, and keeps its files on gefs under `/adm/secstore`. A
  user's secstore password is set with `auth/secuser`.
- In a session, the user's own factotum is started by `lib/profile`, as `newuser` writes it for
  `service=cpu`: drawterm, which has already fetched the user's `factotum` file from secstore,
  gives it at `/mnt/term/dev/secstore` and the profile loads it into `auth/factotum -n`;
  otherwise `auth/factotum` fetches the file from secstore itself, asking for the secstore
  password on `/dev/cons`.

Factotum's protocols are ported as they are needed: `p9any`, `dp9ik` (from `p9sk1.c`) and `pass`
first. `p9sk1` is left out, since Fog's auth server speaks only dp9ik.

## The network device

Fog's `/net` is ip(3)'s file interface over the host's sockets, as drawterm's is: drawterm
serves `/net/tcp` from `kern/devip.c` and calls `kern/devip-posix.c` or `kern/devip-win32.c`
for the sockets, and Fog calls `System.Net.Sockets`, the same on Linux, macOS and Windows.
Fog is an application on its host, so like drawterm it has no IP stack of its own: the host's
system routes, keeps the addresses and decides which ports a process may take. An `announce` or
`connect` the host refuses fails with the host's error.

`#I` serves `/net/tcp` first, then `/net/udp`:

| File | What it does | Under it |
| --- | --- | --- |
| `clone` | Opening it reserves a conversation and opens its `ctl`; reading that gives the conversation's number | a new conversation |
| `n/ctl`, `connect addr!port` | Calls out | `Socket.ConnectAsync` |
| `n/ctl`, `announce [addr!]port` | Takes calls at the address | `Socket.Bind` and `Listen` |
| `n/ctl`, `hangup` | Ends the conversation | `Shutdown` and `Close` |
| `n/listen` | Opening it waits for a call and opens the new conversation's `ctl` | `AcceptAsync` |
| `n/data` | The conversation's bytes | `ReceiveAsync`, `SendAsync` |
| `n/local`, `n/remote` | `addr!port` of each end | `LocalEndPoint`, `RemoteEndPoint` |
| `n/status` | The conversation's state | the socket's state |

A conversation's files belong to the process that opened `clone`, with devip's modes. A note
interrupts a process waiting in `listen`, `connect` or a read of `data` by cancelling the socket
call, which then fails "interrupted"; the mount driver needs the same for a Tflush. The
concurrency is devip's: a lock per conversation, and the conversation's queues, as the rest of
the kernel ports 9front's.

`ndb/cs` is ported from 9front and mounted on `/net/cs`, reading `/lib/ndb/local` from gefs, so
`dial` and `announce` from libc, `aux/listen` and the commands that call them work unchanged.
It finds the addresses of a DNS name with .NET's `Dns.GetHostAddressesAsync` in place of
`ndb/dns`.

## What the kernel needs

The scripts need what the kernel does not have yet:

- **A mount driver.** `mount` of a descriptor that holds a 9P conversation, as devmnt does:
  Tversion `9P2000` with an msize of at most `IOHDRSZ+32768`, Tattach as the process's user with
  no afid under `-n`, one reader of the conversation at a time handing each reply to the request
  that sent it, and a Tflush for a request a note interrupts. Its concurrency is devmnt's, as the
  rest of the kernel ports 9front's.
- **Notes.** Each process has at most five notes waiting (NNOTE) and belongs to a note group,
  which rfork's `RFNOTEG` makes new. Posting a note interrupts a kernel call the process is
  blocked in, which then fails "interrupted". A program may catch notes; one that does not exits
  with the note as its status.
- **`/proc`.** `/proc/{pid}/args`, `note`, `notepg` and `noteid`, as devproc serves them. A
  process may post to its own note group even as `none`.
- **rc's traps.** `sighup`, `sigint`, `sigkill` and the rest, `interrupted`, and `wait` returning
  when a note interrupts it, as `plan9.c` does.
- **`/net`.** `#I`, as above, with `ndb/cs` and `aux/listen`.
- **Capabilities and `/srv`.** `#¤` with `caphash` and `capuse`, and `#s`, where a process posts a
  descriptor for others to mount.
- **gefs as the root.** The host boots the kernel on a gefs store, as `bootrc` mounts
  `/srv/boot` on `/root` and binds it on `/`: `/bin`, `/usr`, `/adm`, `/lib` and `/tmp` are its
  files, and the kernel's devices are bound over it as they are now. The store is a file on the
  host's disk, opened with `GefsServer.Open` when it exists and made with `GefsServer.Create`
  when it does not. Managed programs are written into its `/bin` at each boot, as they are now
  into memory, and an application copied into `/bin` stays there after the host restarts.
- **Commands.** `mount`, `rm`, `test` and `read`, ported from 9front, with `bind`, `cat` and `echo`
  as they are; `tlssrv`, `aux/listen`, `ndb/cs`, `auth/factotum`, `auth/secstore`, `auth/secuser`
  and `auth/secstored`.

## The client

`fog` is drawterm's `rcpu` for a .NET console:

1. Dial the host on 17019.
2. p9any as the client, dp9ik only, fetching the ticket from the auth server for the host's
   domain: by default the host itself on 567, since Fog is its domain's auth server. The password
   is read from the terminal without echo. This client role is added to dp9ik.net, next to its
   server role.
3. TLS-PSK as the client, keyed by the session secret.
4. Send rcpu's script, `dir`, `cmd` and `fn server` as rcpu writes them, preceded by their length.
5. Serve 9P on the conversation, as `exportfs -r /` does, from a tree of its own:
   - `/dev/cons`: reads are the terminal's lines, in the console's own cooked mode; writes go to
     the terminal;
   - `/fd/10`, `/fd/11`, `/fd/12`: the local standard input, output and error;
   - `/mnt/cpunote/data`: an interrupt from the terminal (Ctrl-C, or Ctrl-Break on Windows) is
     read here as `interrupt`;
   - `/env/rstatus`, `/env/rfailed`: rc's status, as the server's script writes it;
   - `/root`: the local file system, with the local user's own permissions.
6. When rc ends and the conversation closes, exit with rc's status as above. If the connection
   is lost first, print "lost connection" and exit 1.

It is a dotnet tool, so `dotnet tool install -g` gives the same `fog` command to PowerShell and
to any Unix shell. Text and bytes cross the connection; PowerShell objects do not.

## Divergences

- `ndb/cs` asks the host's resolver for DNS names instead of `ndb/dns`.
- Factotum's `-S` reads the host owner's key from the host's sealed key store, not NVRAM.
- The root is one host's gefs store until it is replicated, as the architecture describes.
- Fog has no `$cputype`: in place of `bind /$cputype/bin /bin`, the store's `/bin` is bound on
  `/bin` with creation, so a program copied there is kept. `#/` has no `boot` directory.
- The store is served in the host's own process rather than by gefs speaking 9P on
  `/srv/boot`; its files stat as the mount driver's, device `M`.
- Until the store's `/adm/users` is read, each user is the only member of its own group.
- The mount driver names files by qid, as the rest of Fog's namespace does, so it holds a fid for
  an open file but only a bounded cache of fids for files it has walked to, walking to one again by
  its names from the attached root when needed.
- A mount connection stays open until its server hangs up; unmounting does not close it.
- A walk the server refuses reports that the file does not exist, rather than the server's words.
- `mount` with an authentication descriptor waits for factotum (slice 6).
- A note reaches a process as one of its kernel calls returns, or interrupts one it is blocked in;
  a program that makes no calls does not see a note until it makes one, since a managed program
  cannot be stopped between instructions as 9front's clock interrupt stops one.
- `#p` serves `args`, `note`, `noteid` and `notepg` for each process, and lists no `trace`.
- `fog` has no `-p` (aan); a dropped connection ends the session.
- drawterm with its keyboard (`/dev/kbd`) runs `aux/kbdfs` on the server, which Fog does not
  have yet: drawterm works with `-B`, and until kbdfs is ported Del does not interrupt from it.

## Order of work

1. The host boots the kernel on gefs.
2. The mount driver.
3. Notes, `/proc` and rc's traps.
4. `mount`, `rm`, `test` and `read`.
5. `#I` with tcp, then `ndb/cs` and `aux/listen`, checked against 9front's `dial` and
   `aux/listen` on the oracle.
6. `#¤` and `#s`, and factotum with `p9any`, `dp9ik` and `pass`.
7. `tlssrv` and `tcp17019` under `aux/listen`, accepting stock `rcpu` from the 9front oracle.
8. `secstored`, `secstore` and `secuser`, checked against 9front's in both directions.
9. dp9ik's client role in dp9ik.net.
10. `fog`.
11. A first session that makes sense: the user's `$home` exists, `lib/profile` is `newuser`'s,
    with the user's factotum loaded from secstore, and `ls /bin` shows the installed
    applications.

Each slice follows the usual gate: features, then failing tests, then code, with mutation, and
Coyote for the concurrency of the mount driver, `#I`, notes and factotum's processes.

## Evidence

[Login.feature](Login.feature) gives the behaviour end to end. The oracle is 9front itself: the
QEMU harness the namespace interop tests use runs stock `rcpu` against Fog's host, and the bytes
`fog` sends before it starts serving are compared with the script stock `rcpu` sends for the same
directory and command.
