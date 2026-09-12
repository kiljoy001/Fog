# Namespace authorization and policy lifecycle

Status: proposed, single-operator foundation. This is resource authorization, not
a new identity provider or a universal distributed capability-token format.

## Effective authority

An authenticated attach selects its principal's configured namespace profile.
Build it using the existing process-group/mount-table machinery; clients cannot
supply a process ID, group ID, privileged root or replacement profile as authority.
Each new user attach owns an independent group. A job receives a frozen, separately
owned namespace derived from that view, never a reference to a live mutable mount
table shared with another principal. No remote mount/bind administration is in v1.

Start from an empty namespace. Add only configured export roots and granted mounts.
Clamp `..` at the exported root. A mount entry identifies a resource by its stable
`(provider, device, object)` identity; a textual path is only its visible name.
Read-only mounts attenuate rights. Union ordering and create selection preserve
existing Plan 9 semantics and cannot bypass any member's resource permissions.

Authorization is the intersection of:

1. An enabled authenticated principal and current authority generation.
2. Explicit user/group resource grants.
3. Server-enforced owner/group/other permissions and a valid open mode.
4. The export/mount's restrictions and the operation's application invariants.
5. For jobs, the admitted job scope and remaining limits/lease.

An allow at one layer does not override another layer's denial. Group membership
is direct and explicit, not recursive. Do not infer it from matching a username
and group name or copy lib9p's deliberately simplistic `hasperm` helper as a full
group implementation. There is no implicit host-owner bypass for remote users.

Resource hosts check a trusted authorization context, not an untrusted serialized
`User` string. The existing `ResourceOperationContextModel` is a routing/data model,
not proof that its producer was entitled to that identity. The future adapter must
bind it to a trusted issuer, policy generation and operation scope.

## Ordinary resource semantics

Resource rights are `stat`, `walk`, `read`, `write`, `create`, `remove`, in that
canonical order when present. A grant names either exactly one object (`self`) or
that object and its verified descendants (`tree`). Resource providers must support
ancestry checks for tree grants or reject those grants; string-prefix tests and
caller-supplied parent identities do not establish containment. No wildcard provider
or device names in v1. There are no explicit deny rules: absent rights are denied,
matching allows are unioned and then intersected with the other layers above.

Walk checks directory traversal permission at each boundary, including mount and
union transitions. Directory reads omit unauthorized entries; stat and direct
lookups cannot reveal hidden objects merely because their names are known. Create
checks parent rights/mode and constrains requested child permissions as in Plan 9.
Remove checks directory authority and provider invariants. A read-only view denies
write, truncate, create, remove and control-file writes before dispatch.

Ordinary open pins a handle to the object, effective rights and open mode. Subsequent
rename, mode change, or unmount alone does not revoke that handle, matching Plan 9.
Tree containment is proved when granting/opening the handle; moving the object
later does not transform the retained grant into authority over its new siblings.
New lookups use the current tree and permissions. Explicit policy revocation is
different and invalidates retained grants as described below.

The virtual namespace is not an OS security boundary. Providers running arbitrary
native code are trusted installed host code. Untrusted AngouriMath/WASM and other guests
require a verified runtime or isolated process with no ambient filesystem, process,
socket, inherited credential, or service-container escape. If that containment
cannot be established, registration/admission fails before guest execution.

## Job authority

Only the authenticated owner may stage, start, inspect, cancel or release a job in
v1. Job sharing and remote operator impersonation are not implicit features. Clone
allocation charges the owner's staging quota, and no client-supplied `user` field
changes ownership. Provider execution permission is an additional check at start;
write access to `ctl` does not grant every installed provider.

Admission computes and freezes the job scope from the submitting principal's
current grants, namespace profile, selected provider/version, artifact identities
and limits. Changes after admission never widen that scope. Source, artifacts and input
handles are read-only; result staging is bounded and private. Extra namespace IO
requires an explicit grant. Preparation cannot perform externally visible writes.

The control node registers a host-owned scope ID associated with the admitted job,
assigned worker incarnation, policy epoch and lease. Internal operation contexts
carry that reference plus operation identity. Resource hosts accept them only over
an enrolled internal-node channel and validate the registration and its scope;
unknown scope references, changed users, wrong worker/job or expired authority fail
closed. Guests cannot construct or export those contexts. The node's internal
certificate is not itself permission to spend arbitrary user quotas.

The concrete scope package, lease exchange and `scope:<id>` internal 9P attach are
specified in [WorkerControl.md](../fog-v1-profiles/WorkerControl.md). A scope attach
is a node-only export exception, not permission to supply arbitrary attach authority.
The initial process boundary is [linux-process-v1](../fog-v1-profiles/Isolation.md).

This is a trusted single-operator mechanism, not a credential users can hand to
third-party machines. No user login proof, private key or general-purpose bearer
token travels with the job. Guest attempts to access `/transport`, `/control`,
policy administration, factotum, or `/compute/clone` are denied. Nested jobs are
explicitly unsupported in v1, even if the submitter can create ordinary jobs.

## LibTab configuration bundle

Policy is an immutable local-directory bundle with these fixed files and schemas.
All use plain LibTab columns, strict validation before deduplication, LF UTF-8,
finite row/document limits and no unknown fields. Numbers are unsigned decimal;
booleans are exactly `true` or `false`. Identifier grammar follows Authentication.md.
All references must resolve within the bundle or to explicitly installed resources.

| File / schema | Columns in order |
| --- | --- |
| `policy.tab` / `fogpolicy-v1` | `epoch`, `authdom`, `server`, `principals_sha256`, `keys_sha256`, `groups_sha256`, `mounts_sha256`, `grants_sha256`, `execute_sha256`, `quotas_sha256` |
| `principals.tab` / `fogprincipals-v1` | `user`, `enabled`, `namespace`, `quota` |
| `keys.tab` / `fogkeys-v1` | `key_id`, `user`, `algorithm`, `public_key`, `enabled` |
| `groups.tab` / `foggroups-v1` | `group`, `user` |
| `mounts.tab` / `fogmounts-v1` | `namespace`, `order`, `path`, `provider`, `device`, `object`, `placement`, `readonly` |
| `grants.tab` / `foggrants-v1` | `subject_kind`, `subject`, `provider`, `device`, `object`, `scope`, `rights` |
| `execute.tab` / `fogexecute-v1` | `subject_kind`, `subject`, `runtime`, `provider_version` |
| `quotas.tab` / `fogquotas-v1` | `quota`, `staging_jobs`, `queued_jobs`, `running_jobs`, `staged_bytes`, `memory_bytes`, `retained_bytes`, `max_deadline_ms`, `max_output_bytes` |

`policy.tab` has exactly one explicit row. The named hashes are lowercase SHA-256
of the exact canonical companion-file bytes. They provide coherent bundle identity,
not authenticity: the directory is installed by the trusted local operator with
OS-protected ownership. The root policy file's own hash identifies the whole bundle.
Symlinks, mutable files during validation, unknown files, unresolved references,
duplicate identities and identical duplicate rows all fail validation.

`subject_kind` is `user` or `group`. `scope` is `self` or `tree`. A rights list is
comma-separated, ordered, nonempty, with no spaces or repeats. `object` is the
resource's unsigned 64-bit Qid path, not a visible pathname. Quotas are positive;
disable a principal instead of giving it an unlimited or ambiguously zero quota.
Memory is summed across reserved jobs; retained bytes are budgeted by reserving
the maximum accepted output until terminal cleanup/expiry releases it.

Mount paths are absolute, normalized component paths: no empty interior components,
`.`/`..`, trailing slash except `/`, backslash, NUL, or variable expansion. Reject
overlong components. `order` is unique within each namespace and defines ascending
construction order. `placement` is `replace`, `before`, or `after`, mapped to the
existing mount flags; v1 configuration does not enable union create propagation.
Exactly one root replace entry must precede other mounts. The service never executes
a namespace file as shell commands. None of these files contains a private key.

This data format is proposed independently of provider-owned resource persistence.
Provider resource IDs and their wire Qids must still be globally non-colliding in
the composed export, as required by the existing namespace contract.

## Updates and explicit revocation

V1 administration is local to the control host, through an OS-protected 9P socket.
Remote user grants cannot access it. Stage a complete immutable bundle out of band;
`check N\n` validates generation N without activation. `apply N\n` names that exact
prevalidated bundle and checks its digest again. Each control Twrite is one complete
bounded LF-terminated command; rejected commands have no policy side effect.

Only strictly increasing epochs can be activated. Persist the new epoch and bundle
identity atomically before acknowledgment. A crash before this commit preserves
the old generation; a crash after it recovers the new one. Missing/corrupt committed
configuration is a startup error, never a reason to fall back to an older policy.
Reapplying the identical committed generation is idempotent; different content
under the same number is rejected. Skipping a number is allowed.

For simplicity, every applied generation explicitly revokes all older-generation
authority, not just changed users. Stop new admissions during the transition.
Invalidate pending authentication attempts, attached roots, open user handles and
internal job scopes from the preceding generation. Queued jobs fail `policy-changed`;
running jobs stop through containment before publishing a terminal failure. Effects
already committed are not undone. Previously completed results remain available
only after fresh authentication and ownership checks under the new policy.

The control node stops renewing old-generation worker leases immediately. Connected
workers acknowledge installation and shutdown of old scopes; disconnected workers
lose authority at their already bounded lease expiry and stop independently. Control
reports `revocation_pending=true` until every old lease is acknowledged stopped or
its conservative expiry bound has elapsed. It must not claim instantaneous global
revocation during a partition. No new-generation admissions begin before that barrier.
This deliberately disruptive v1 rule makes privilege changes auditable; finer-grained
live revocation is a later profile.

Resource acceptance and local revocation have a serialized boundary. An operation
accepted before that boundary may complete with effects; revocation is not rollback.
An old-scope operation arriving afterward is rejected. During a partition, a remote
host's already granted lease bounds this window, which is why global revocation
remains pending. Closing user sessions does not falsely report an in-flight mutation
as never having happened.

Key rotation can stage old and new keys together, then remove the old key in a later
epoch. Removing a key is meaningful only with epoch/session invalidation, not just
editing a public-key list. Lost client keys are recovered by local operator enrollment;
there is no account-recovery network endpoint or automatic trust-on-first-use.

## Reference behavior

[Plan 9 open semantics](https://9p.io/magic/man2html/5/open) preserve rights of
already-open files after ordinary permission changes. The additional authority-epoch
revocation above is explicit. [Namespace descriptions](https://9p.io/magic/man2html/6/namespace)
inform construction, not the LibTab schema or a promise of a host OS sandbox.
