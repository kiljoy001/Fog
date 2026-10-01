# linux-process-v1 execution boundary

Status: proposed first supported containment profile, not a claim that the current
gateway can safely execute untrusted code. Installed host adapters, supervisors and
runner binaries are trusted. Uploaded WASM and input files are not.

## Host prerequisites and ownership

Support Linux x86_64 and aarch64 with cgroup v2 memory/pids/cpu controllers,
`cgroup.kill`, pidfds, user/mount/PID/network namespaces and seccomp filters. The
host validates actual functionality at startup, not just the kernel version string.
Missing delegation, disabled controller/filter, unsupported architecture or inability
to create the required boundary makes execution ineligible. Never fall back to an
unrestricted in-process provider because sandbox setup failed.

A dedicated trusted `fog-supervisor` runs independently of the Orleans host process
and its scheduler/GC. An OS-protected Unix socket exposes local 9P administration to
the configured node-host UID only, checked with peer credentials. This socket is
not mounted into user/job namespaces. Necessary privileged setup is confined to
that supervisor/helper; no guest receives host capabilities or a privileged UID.

Use one execution process per job, no warm mutable worker pool. The supervisor
owns the process pidfd, cgroup, namespace setup, deadlines, byte reservations and
teardown. The node host cannot publish terminal success merely from a child's exit
message. The supervisor reports whether it actually observed exit and reconciled
owned resources. An execution key `(control_boot,job,worker_boot,scope)` is unique
for the host boot; duplicate launch/run commands never create a second process.

All local deadline calculations use Linux CLOCK_BOOTTIME, including suspend time.
Only local supervisor messages carry absolute boottime values; network leases use
request-relative timers and the existing bounded clock-rate error contract. Check
expiry before guest work resumes after host suspension. AAN cannot pause these clocks.

## Local supervisor interface

The private socket exports `/clone` and `/<id>/{spec,ctl,status}`. Clone has the same
one-ID-per-open and bounded staging behavior as the job interface. Spec is a sealed
one-row `foglaunch-v1` LibTab document:

`job,scope,node,worker_boot,control_boot,policy_epoch,bundle_sha256,profile,
deadline_ns,lease_ns,memory_bytes,output_bytes,artifacts_sha256`

`profile=linux-process-v1`. Deadlines are absolute positive CLOCK_BOOTTIME nanoseconds;
the node host computes them from the original request/grant, not the time of local
forwarding. The supervisor rejects already expired values, mismatched execution
keys and values exceeding the configured host bounds. Artifact/bundle identities
must resolve through its trusted installed registry and per-job capability broker,
never a supplied executable path or shell command.

Ctl commands are exactly `launch\n`, `stop\n`, `release\n` or
`renew <positive-lease-seq> <absolute-lease-ns>\n`, one bounded Twrite. Launch freezes
spec and is idempotent for that execution key. Renew requires the next sequence,
old lease still live, same job/boot/epoch, and no extension beyond deadline_ns;
duplicate same sequence/value is a no-op, changed duplicate or expired lease fails.
Only the trusted node host can forward verified control grants. Stop is idempotent;
release rejects a still-running/unreconciled execution.

Status is per-open `fogprocess-v1`:
`job,scope,state,lease_seq,exit_code,signal,memory_peak,output_bytes,reaped,error`.
State is `staging`, `starting`, `ready`, `running`, `stopping`, `exited`, `failed`.
Unavailable values are nil. `reaped=true` is supervisor evidence, not child text.
Parent crash, EOF or local control loss does not extend a lease; the independent
supervisor still stops/reaps at its existing deadline.

## Child launch and confinement

Before exposing source/artifacts/input to any parser:

1. Verify the immutable runner bundle and create a unique owned cgroup. Set
   memory.max to memory_bytes, memory.swap.max to zero, memory.oom.group to one,
   pids.max to worker_pids, and cpu.max to a finite host-configured quota with a
   100000-microsecond period. Add positive `worker_cpu_cores` to foglimits-v1;
   quota is checked worker_cpu_cores * 100000, capped by enrolled host capacity.
2. Enter fresh PID/user/mount/network namespaces. Use a dedicated unprivileged UID,
   drop all capabilities, set no_new_privs, and ensure mount propagation is private.
   No interface/routes except isolated loopback, and no permission to create sockets
   after startup. There are no GPU/NPU/device passthrough nodes in this profile.
3. Construct a read-only minimal root containing only verified runtime dependencies
   and permitted immutable artifacts, plus bounded private tmpfs at `/tmp` mounted
   nosuid,nodev,noexec. The root/artifact mounts are read-only,nosuid,nodev.
   No host root, credentials, home directories, Docker socket, cgroup control tree
   or supervisor socket. A proc mount, if needed by the pinned runtime, sees only
   the new PID namespace; no host proc/sys tree is bind-mounted.
4. Set finite rlimits for file descriptors, file size, core dumps (zero), stack and
   CPU time. CPU hard seconds are ceil(remaining deadline milliseconds / 1000) + 1;
   the independent wall/lease watchdog remains the tighter bound. Temporary files
   charge worker_tmp_bytes and memory; no unbounded spill to host disk.
5. Close all inherited descriptors except the two approved private 9P channels and
   required isolated standard streams. Launch the exact verified runner with fixed
   arguments `--control-fd 3 --capability-fd 4`; no guest text in argv/environment.
   Initialize trusted runtime dependencies, then install the final seccomp policy
   before accepting any untrusted source/artifacts/input. Readiness requires an explicit
   supervisor-observed seal acknowledgement from the trusted runner.

Forbid host/network filesystem mounts and namespace escape syscalls after setup.
The [cgroup v2 controls](https://docs.kernel.org/admin-guide/cgroup-v2.html) provide
resource enforcement; they do not by themselves restrict filesystem/syscall access.
Use [seccomp filters](https://docs.kernel.org/userspace-api/seccomp_filter.html)
together with the namespace/credential restrictions, not as a complete sandbox alone.

## Syscall policy contract

The runtime lock includes the exact installed seccomp-policy digest for its platform.
Only these syscall families may appear in its allowlist, narrowed to the runner's
actual needs: file IO/stat/path lookup within the confined mount tree; descriptor
duplication/poll/epoll/pipes/eventfd; anonymous/file-backed memory and protection;
signals/futexes/thread-local setup; clocks/sleep/random; process/thread identity and
read-only resource/CPU queries; thread creation under the rule below; exit/exit_group.
No unspecified syscall is allowed: the compiled per-architecture list is a verified
bundle input and unknown syscall numbers terminate the process. Its canonical
`fogsyscalls-v1` manifest contains `architecture,name,argument_rule`, one row per
permitted syscall; reject duplicates and names outside these families at registration.

Mandatory hard denials cannot be overridden by that list: socket/connect/accept and
network send operations; ptrace/process_vm; bpf/perf/io_uring; keyring/module/kexec;
mount/umount/pivot_root/chroot/setns/unshare; credential/capability changes; execve/
execveat after the startup seal; arbitrary ioctl and device access. Path confinement
comes from the read-only mount tree and unprivileged UID, not string filtering in BPF.
Any write-capable file operation is confined to private tmpfs or approved descriptors.

clone3 returns ENOSYS so supported runtimes can use the auditable clone path. Clone
permits only thread creation with CLONE_VM|CLONE_SIGHAND|CLONE_THREAD and the pinned
runtime's explicitly listed thread/TLS flags; reject process creation, namespace
flags and flags outside that exact mask. pids.max includes threads. After the seal,
deny setrlimit; permit prlimit64 only as a query with a null new-limit pointer.
All changes to rlimits happen in trusted setup, since seccomp BPF cannot inspect
a pointed-to limit structure to prove a requested change only lowers bounds.
Signal delivery is confined to
the child PID namespace/owned thread group. If a runtime cannot initialize/run under
this policy, reject that bundle rather than permitting an escape syscall dynamically.

## Two child 9P channels

FD 3 is a socketpair with the supervisor as 9P client and trusted runner as server.
After standard version/attach (`aname=worker`, NOFID on this private descriptor only),
the runner exports `/spec`, `/ctl`, `/status`, `/result`, `/stderr`. Spec is the frozen
job's LibTab document; the supervisor uploads and seals it. Ctl accepts exactly
`prepare\n`, `run\n`, `stop\n`, one command per Twrite. Prepare constructs one execution;
run is allowed once after preparation and control-host permission; stop must progress
while run is blocked. The runner services protocol/control on a separate execution
path from guest work. Its status uses `fogrunner-v1`:
`job,phase,finish_reason,error,result_bytes`, with phase `new`, `prepared`, `running`,
`stopping`, `done`. Child status is advisory, not authoritative cleanup evidence.

FD 4 is a separate private socketpair with the trusted runner as 9P client and the
host capability broker as server (`aname=job`, NOFID on this descriptor only). It
exports `/input`, `/artifacts/<field>` and `/ns` for the already frozen scope. No
arbitrary user/owner/job selection is accepted. The broker associates the descriptor
with the execution key at creation and applies scope, lease and IO limits on every
operation. Remote namespace calls use the scope-bound node 9P attach; the child has
no node TLS key. The initial WASM guest ABIs expose only their documented
input/output/artifact operations, not raw access to either descriptor.

These private channel trust exceptions are never accepted on a TCP listener. No TLS
or AAN is needed on the unexported socketpairs; local service traffic remains 9P.
Guest input bytes cannot be reinterpreted as supervisor commands or child status.
The host never serializes a Stream, provider instance or CancellationToken into a
remote grain argument.

## Stop, reap and publication

Before run, recheck all deadlines and the unique run permit. Cancel/lease/deadline,
OOM, protocol violation or exhausted output triggers stop. Revoke the broker first;
request cooperative stop, then use cgroup.kill and pidfd-based observation within
stop_ms. Never kill an unrelated process based on a recycled numeric PID. Install
parent-death protection with a parent-identity recheck; supervisor restart reconciles
only its recorded owned cgroups/processes before advertising capacity.

Success requires complete verified output, valid host completion authority and
reaping of execution, plus disposal of mutable contexts/descriptors/tmpfs. The
supervisor may retain bounded output bytes only. Outstanding guest resource calls
must be completed or have their waits/channels cancelled and authority reconciled;
an uncertain already accepted write is not reported as rolled back. Do not leave
queued old-scope writes able to enter a replacement logical connection.

Kernel-uninterruptible work can defeat immediate process termination. At the hard
deadline, if exit/reaping cannot be established, mark the host failed/quarantined,
keep its reservations unavailable, and report cleanup-failed rather than publishing
success, claiming it stopped, or reusing its slot. Operator fencing/reboot may be
required. A finite timeout is a detection bound, not proof the kernel can always kill
any task instantly. Restart must reconcile that ownership before new admissions.

`Isolation.feature` requires real Linux mechanism probes, malicious guest escape
attempts, blocked native work, parent death, PID reuse, process-resource cleanup and
no success before reaping. Also fuzz both private 9P roles and mutation-test the
supervisor's authority, deadline, run-once and publication decisions.
