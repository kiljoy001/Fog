# Namespace experiments

The current milestone emulates Plan 9 namespaces over local and Orleans-backed
resources. Useful experiments exercise the same operations through ordinary 9P
clients and the process control files:

- Bind two resource directories into a union and verify walk and directory order.
- Copy a process namespace, change one mount, and verify isolation from the parent.
- Share a process group and observe a mount change from both processes.
- Keep a fid open across unmount and verify channel lifetime independently of lookup.
- Cancel a blocked request and verify other fids and subsequent requests still work.
- Issue bind, unmount, and rfork through `/proc/<pid>/ctl` and inspect `ns` and `status`.

The [BDD contracts](specifications/plan9-namespace/README.md) define the behavior;
the [implementation notes](plan9-namespace-semantics.md) distinguish completed work
from remaining compatibility gaps. Workload execution is deferred.
