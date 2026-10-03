# Authenticated AAN session establishment

Status: proposed `fog-aan-v1`. This completes the bootstrap/resume contract for
[stock AAN below 9P](../swarm-9p-transport/Aan.md). It requires cryptographic and
protocol review before production. Stock AAN record compatibility does not imply
an unmodified stock client implements this fog authentication envelope.

## Two phases on one protected carrier

```text
fresh TCP + validated TLS
  -> ordinary 9P bootstrap, aname=aan
  -> authenticated claim + quiescent switch\n / Rwrite barrier
  -> stock AAN records carrying the logical 9P session
```

Every physical reconnect repeats the bootstrap on a fresh TLS connection. There
is no separate HTTP service, dynamic reconnect port, TLS early data, unprotected
AAN header, or raw Orleans stream. Endpoint configuration selects `direct` or
`fog-aan-v1`; both begin with ordinary 9P inside TLS, but only the latter permits
the `aan` export and switch. A required profile cannot fall back to direct mode.

The bootstrap 9P session is disposable and separate from the retained logical
session. Its msize is negotiated independently, at least 256, at most the normal
host limit. Bootstrap fids/tags/afids never become logical fids/tags/afids.

The user parts of this profile were written against `fog-auth-v1`, the signed-cell
login that [Authentication.md](../fog-foundation/Authentication.md) has since replaced
with dp9ik on the afid. They must be redesigned for dp9ik before user AAN sessions are
supported; the node parts are unaffected.

As written: for users, extend `fog-auth-v1` to permit `Tauth.aname=aan` **on this
bootstrap profile only**. All fields, exact-byte signing, proof limits and validation remain
as in Authentication.md, with `aname=aan` included in the signed body. A `fog`
proof cannot attach `aan` or vice versa. The client's factotum policy must explicitly
permit both purposes; do not broaden its key selection silently.

For nodes, mutually authenticated TLS plus `Tattach` with `NOFID`, enrolled node
`uname` and `aname=aan` selects the node-bootstrap root. This is never allowed on
the user listener. Both bootstrap roots are host-local, available without Orleans.
Bootstrap authority gives no job, scope or runtime-channel access.

## Bootstrap namespace

Paths are relative to the `aan` root:

| File | Behavior |
| --- | --- |
| `/clone` | Open allocates one bounded pending logical session; read its 64-hex random ID plus LF |
| `/sessions/<id>/status` | Owner-only immutable per-open `fogaansession-v1` snapshot |
| `/sessions/<id>/claim` | Open RDWR allocates a fresh per-fid claim challenge; reads return that challenge; writes supply one proof |
| `/sessions/<id>/tunnel.<claim-fid>` | Open RDWR pins that verified claim as a switch handle; write `switch\n` to enter the AAN data phase |

The root does not enumerate other owners' sessions. Missing and foreign IDs return
the same bounded error. Clone pins one ID; repeated reads do not allocate. User
ownership is `(authdom,user,key_id)` using the bootstrap proof's enrolled key;
node ownership is its enrolled node ID and TLS SPKI pin. One owner per session.
Pending clone allocations expire under `handshake_ms`, including time spent signing.
Creation grants no indefinitely resumable unauthenticated stream.

`fogaansession-v1` columns: `session,server_boot,owner_kind,owner,generation,state,
policy_epoch,resumable`. States: `pending`, `connected`, `suspended`, `terminated`.
No proof, nonce, credential or retained payload appears in status. Missing/expired
sessions need not retain a tombstone: IDs are unpredictable and never reused.

At creation the logical export is fixed by owner kind: `fog` for a user, `runtime`
for a node. Only that owner may authenticate/attach inside the logical session.
A user cannot add another user's afid. Initial inner negotiation/authentication
uses the existing profile; the first successful authorized inner attach makes the
session resumable. A break before that commit terminates it. A lost first Rattach
may be recovered if the server did commit and the client retained its local state.
Version reset invalidates inner state and disables resumption until a new matching
attach; it cannot reset the original absolute session lifetime.

## Claim statement and proof

Each opened claim fid pins exactly one canonical `fogaan-v1` row, column order:

`profile,action,server,server_boot,authdom,owner_kind,owner,key_id,logical_aname,
session,base_generation,carrier,nonce,server_cert_sha256,policy_epoch,ttl_ms`

`profile=fog-aan-v1`; `action=new` only for pending never-switched sessions, otherwise
`resume`. `owner_kind=user|node`; node `authdom` and `key_id` are semantic nil.
`carrier` is 32 fresh random bytes encoded as lowercase hex for this bootstrap
connection; `nonce` is an independent 32-byte per-claim random value. `server_boot`
and `session` identify the exact retained endpoint. `base_generation` is its current
uint64 generation, initially zero. TTL begins at claim open, is at most
`challenge_ms`, and is additionally bounded by all original session deadlines.

Challenge maximum is 2048 bytes. The client checks schema, configured server/domain,
owner, requested action/session, current TLS leaf SHA-256, original server boot,
generation and policy before approving it. For `new`, boot/session must match the
newly allocated status over this validated carrier. For `resume`, match the client's
retained session record; never adopt a new boot merely because a peer supplies it.

User proof: the same `fogproof-v1` `key_id,proof` envelope as login, with a `SIGNED`
cell over **exact fogaan-v1 challenge bytes** using the enrolled
`libtab-eddsa-blake2b-v1` key through local `monocypher role=sigcell`. Key ID must
equal the session owner key and the bootstrap-authenticated key. The different
signed schema binds intent; ordinary `fogauth-v1` or `tpm9p-user-auth-v1` proofs
are never resume proofs. No signing seed leaves factotum.

Node proof: one `fogaannode-v1` row with `challenge_sha256`, the SHA-256 of the exact
challenge. The authority is the freshly verified TLS node identity, not this hash.
The server requires the matching claim to belong to this same bootstrap connection
and enrolled node. No equivalent hash-only path exists for users.

Proof writes use contiguous offsets, at most 4096 total bytes, and the canonical
final empty line seals the document. Intermediate Rwrites acknowledge buffering;
the final Rwrite acknowledges verification/consumption, not carrier takeover.
Malformed/failed completed proofs invalidate the claim. Reads still return the
original challenge. Do not replay a proof write after an ambiguous Rwrite; attempt
the switch with that verified claim or start a fresh bootstrap/claim.

V1 requires the same actual server TLS leaf-certificate digest on resumption as
at session creation, as well as current pin/enrollment validity. Certificate rotation
therefore requires fresh logical sessions; it cannot silently reinterpret pending
inner challenges against a different certificate. This deliberate restriction
preserves the existing endpoint-bound login profile. It is not TLS-exporter binding
and makes no claim after an enrolled endpoint's keys/host are compromised.

## Switch barrier and competing claims

Associate tunnel open with one verified claim on the same bootstrap connection;
only one verified claim/tunnel pair may remain on that connection. Its claim fid
number is supplied in the tunnel walk name `tunnel.<decimal-claim-fid>`, not an
ambiguous implicit last-claim selection. Wrong,
unverified, foreign, expired or already consumed claim fids are rejected.

Before `switch\n`, clunk all bootstrap fids except that tunnel fid, and wait for all
preceding replies. Clunking the claim after tunnel open does not erase the tunnel's
pinned verified claim. There may be no pending bootstrap request except the switch.
The server also checks these conditions; it does not rely on client politeness.

`switch\n` is one seven-byte Twrite. Its atomic acceptance point rechecks owner,
TLS certificate, server boot, policy epoch, deadlines, resumption eligibility and
`base_generation == current_generation`; then consumes the claim and increments
generation without overflow. Exactly one competing claim at that generation wins.
Only this winning authorized transition fences/closes the preceding carrier.
Invalid claims cannot evict a healthy carrier. Generation exhaustion terminates
the logical session instead of wrapping.

Server sends the complete matching Rwrite as the **last bootstrap frame**, then
hands the same decrypted TLS stream to AAN. Client sends no pipelined bytes and
enters AAN only after decoding that exact complete Rwrite. The dispatcher preserves
any buffered bytes at the handoff; it neither drops them nor parses AAN as 9P.
Pipelined bootstrap requests beyond switch are a protocol violation. No raw
Orleans passthrough is involved: every AAN payload still reconstructs 9P.

A lost/partial switch reply is ambiguous: close that physical carrier, never guess
which parser should consume the next byte. On resume, retry through fresh TLS,
bootstrap and a fresh claim; read current generation and revalidate it against the
same retained session. A generation advance caused by this client's prior ambiguous
switch is acceptable; a different boot/session/owner is not. If local logical state
was lost, resumption is forbidden even when the server retained it. A newly created
session that never became resumable expires; allocate a fresh one, with no job replay.

Bootstrap clunks and switch cleanup do not clunk the retained inner session. Once
switched, Rflush and every other protocol response belong to the inner 9P session.
Closing inner transport, expiry or revocation still tears it down. All original
handshake, authentication, lease, operation and shutdown timers continue throughout.

## Conformance

The independent recorder must decode both bootstrap 9P and the switch boundary,
then stock AAN and inner 9P. Exercise every cut through switch/Rwrite and the first
AAN header, one-way window exhaustion, replay and stale-carrier callbacks. Real
custom factotum tests must verify the exact challenge bytes and the 2048/4096 bounds.
Use `../go-aan` and the recorded 9front source as independent codec fixtures, not as
production authentication substitutes. See `AanSession.feature` for stable cases.
