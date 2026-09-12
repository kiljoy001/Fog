# Orleans 10.3.1 membership over 9P

Status: proposed adapter contract, not a new membership algorithm. Orleans retains
its membership decisions; the control host supplies atomic table operations. The
API baseline is the installed `Microsoft.Orleans.*` 10.3.1 packages and their XML
contracts for `IMembershipTable`, `MembershipEntry`, `TableVersion` and
`IGatewayListProvider`. An Orleans upgrade must re-run the method/field mapping
tests rather than silently ignoring added requirements.

## Service and identity

`/control/membership` under the mutually authenticated `runtime` export implements
[Records.md](Records.md). It runs before grains. One configured control host owns
one table for the configured `(service_id,cluster_id)`; both identifiers obey the
existing 1..64 ASCII identifier grammar. All worker hosts connect to that authority
over 9P, never a hidden database/native Orleans membership socket.

Membership is ephemeral per control boot. Before publishing readiness, create the
empty table with numeric version 0 and a fresh boot-bound table etag. A restarted
control host does not merge old tables or promote another authority. The existing
lease reconciliation barrier still precedes admitting work.

An Orleans silo address is the exact `(IP address,port,generation)` tuple. IP text
must be canonical numeric IPv4 or IPv6 without a scope suffix; IPv4-mapped IPv6 is
normalized to IPv4 at the adapter boundary. Port is 1..65535 and generation is the
Orleans int32 value, encoded as canonical signed decimal. It is not a worker lease
or the 256-bit worker boot ID. A node's configured numeric endpoint supplies IP and
9P port; the adapter never opens a native silo port described by these fields.
This profile requires numeric IPs in fognodes-v1 endpoints; tls_name remains the
separately checked certificate identity. V1 uses the same enrolled node 9P endpoint
for silo and Orleans-client channels, distinguished by their logical handshake.

Insert binds the address to the authenticated enrolled node and its registered
node boot; another incarnation cannot overwrite the same address key. Orleans must
use a fresh generation after restart; a collision fails join rather than aliasing
an old address. Table entries cannot introduce unconfigured destinations. Node
names, host names and timestamps in records confer no authority.

## Exact files and records

Transaction `request` uses `fogmemberop-v1`, one row, columns:

`op,service_id,cluster_id,control_boot,try_init,ip,port,generation,table_version,
table_etag,row_etag,before_ticks`

Allowed operations: `init`, `read-row`, `read-all`, `insert`, `update`, `alive`,
`cleanup`, `delete-all`. Fields not used by an operation are semantic nil.
`control_boot` must match the observed current control boot; `init` obtains it from
the authenticated `/control/info` snapshot defined in WorkerControl.md first.

For insert/update/alive, input `entry` uses `fogmember-v1` with exactly one row:

`node,node_boot,ip,port,generation,status,proxy_port,host_name,silo_name,role_name,
update_zone,fault_zone,start_ticks,alive_ticks,row_etag`

`row_etag` is nil on input; the request carries an expected etag for update.
`proxy_port` is 0 or that same enrolled node 9P endpoint port. Host/silo/role
names are nullable bounded UTF-8 up to 256 bytes with no NUL; preserve nil separately
from empty text. Update/fault zones are canonical signed int32 decimal, preserved
even when zero or not used by the scheduler. These fields are public in the pinned
assembly even where its XML excerpts omit them. `status` is exactly `None`, `Created`,
`Joining`, `Active`, `ShuttingDown`, `Stopping` or `Dead`, mapped by enum name, never
an unchecked numeric cast. Unsupported enum values fail the adapter's conformance.

Time fields are decimal UTC .NET ticks since 0001-01-01, range
0..3155378975999999999, at 100 ns resolution. The adapter normalizes valid DateTime
values to UTC explicitly: UTC is retained, Local uses the sender's DateTime
ToUniversalTime conversion, and Unspecified is rejected except zero ticks, which
is the default missing-time sentinel and is reconstructed as UTC zero. Never guess
from the receiver's timezone. Cleanup's DateTimeOffset converts to UTC ticks.
These values are membership metadata, not
execution lease clocks.

Insert/update also seal `suspects`, a `fogmembervotes-v1` table:

`target_ip,target_port,target_generation,ordinal,suspector_ip,suspector_port,
suspector_generation,vote_ticks`

Rows preserve the exact bounded `SuspectTimes` list using contiguous ordinals from
zero, one target matching `entry`. Duplicate ordinals/gaps fail; repeated suspectors
are not silently deduplicated. Zero votes is a schema with zero rows. Read results
use the same table for all targets, sorted by target tuple then ordinal. `alive`
has no suspects input and cannot mutate votes or other entry fields.

`reply` uses `fogmemberreply-v1`:
`op,control_boot,ok,error,table_version,table_etag,row_count`.
Read outputs `entries` and `suspects` are one pinned atomic table snapshot; `entries`
uses `fogmember-v1`, with server etags. Empty ReadRow returns zero rows plus the
current table version. Unknown row is not an invented MembershipEntry.

Etags are opaque boot-bound strings up to 128 ASCII bytes. Each successful row/table
replacement gets a never-reused etag; version is checked int32 0..2147483647 to fit
`TableVersion.Version`. Overflow fails the operation and makes membership unready
for writes; it never wraps. Read-only snapshots remain available for diagnosis.

## Complete API mapping

| Orleans method | Transaction and atomic semantics |
| --- | --- |
| `InitializeMembershipTable(bool)` | `init`, `try_init` set; verify the configured table exists. True is idempotent creation before use; false never creates. Both return the current version/etag. |
| `ReadRow(SiloAddress)` | `read-row`, address fields set; atomically return matching entry, votes and table version. |
| `ReadAll()` | `read-all`; atomically return all entries, votes and table version. No separate unpinned row reads. |
| `InsertRow(entry, TableVersion)` | `insert`; address must be absent, expected table etag must match, supplied new version must equal current + 1. Commit row, votes, row etag and new table version/etag together. Return true or false for conflict. |
| `UpdateRow(entry, etag, TableVersion)` | `update`; row must exist, row etag and table etag must match, new version must equal current + 1. Replace entry/votes and both etags atomically. Return false for conflict with no partial change. |
| `UpdateIAmAlive(entry)` | `alive`; self-node/address only. Update only alive_ticks, never table version, row etag, status or votes. Store max(existing,incoming) to prevent delayed traffic moving time backward. Missing/deleted address is an idempotent no-op, not resurrection. |
| `CleanupDefunctSiloEntries(beforeDate)` | `cleanup`; remove only Dead entries whose max(start_ticks,alive_ticks,all vote_ticks) is before the cutoff. One atomic transaction; if rows change, advance table version/etag once. Never remove a live row. |
| `DeleteMembershipTableEntries(clusterId)` | `delete-all`; exact configured cluster only, control-node administrative identity, drained host and reconciled leases required. Atomically clear rows/votes and advance version/etag, not drop another cluster or reset counters. |

`MembershipTableData.Members` contains the reconstructed `(MembershipEntry,row_etag)`
pairs; its
`Version` uses the snapshot's numeric version and table etag. Derived effective
timestamps are computed by the pinned Orleans type, not duplicated as mutable fields.
Transport errors/unavailability throw the provider failure; they are not a false
CAS result. Only an actual CAS conflict maps to false. Persist/replay a transaction's
semantic conflict result as well as success.

An update of another member is permitted to an enrolled silo because Orleans must
record suspicion/death decisions about peers. It cannot change that row's enrolled
node/address/boot association. Alive and insert remain self-bound. Destructive
table deletion requires the separate drained control-host condition above.

## Gateways and transport binding

`IGatewayListProvider.InitializeGatewayListProvider` verifies the same table and
configured cluster. `GetGateways` reads one coherent snapshot, selecting `Active`
entries with nonzero ProxyPort and a configured enrolled endpoint. Convert addresses
using Orleans.Runtime.Utils.ToGatewayUri with the gateway port: the representation
is `gwy.tcp://IP:port/Generation`, with normal URI brackets for IPv6. Parse/round-trip
it in adapter tests. This is Orleans' logical address format, not a promise to open
a native TCP RPC protocol. The custom connection factory resolves it only to the configured
TLS/AAN/9P endpoint, never to an unvalidated hostname or native fallback listener.

`IsUpdatable=true`; `MaxStaleness` is the configured positive `worker_poll_ms` interval.
Expire the local gateway snapshot at that age and fail unavailable instead of using
an unlimited stale cache when refresh fails. Orleans may cache within its configured
bound; gateway discovery does not grant worker execution authority.

Silo-to-silo and external Orleans-client transport hooks must both use the existing
`/transport/orleans` stream contract. All native silo/gateway listeners are disabled
in strict mode. The acceptance fixture checks actual listening/dialed endpoints,
not merely whether this provider was registered in dependency injection.

Membership heartbeats are not AAN probes and do not grant job leases. An AAN-delayed
alive record may update only its original address/time, never mark a Dead node Active.
Control reboot invalidates old transactions, row etags and cached gateways; the
adapter reports unavailability and rejoins through fresh negotiation, not raw replay.

## Verification

`Membership.feature` covers each API branch, concurrent CAS and snapshot coherence,
heartbeats without version mutation, cleanup cutoffs, wrong clusters, boot loss and
gateway routing. Bind against the installed interfaces, including public field and
enum coverage. Use real two-silo join/failure fixtures and the full quality gate.
The upstream [interface source](https://github.com/dotnet/orleans/blob/main/src/Orleans.Core/SystemTargetInterfaces/IMembershipTable.cs)
is a secondary navigation reference; the installed 10.3.1 metadata is the pinned
contract, since the main branch can change. Reflection of the installed assembly
confirms the eight interface methods and eleven public MembershipEntry fields;
round-trip every field, not only those used for this profile's scheduling policy.
