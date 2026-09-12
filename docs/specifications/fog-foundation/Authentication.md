# User-side factotum authentication

Status: proposed protocol profile, not an implemented or security-reviewed login.

## Transport and trust

The initial user profile is named `fog-auth-v1`, on standard `9P2000` protected by
TLS 1.3. The direct profile carries 9P immediately inside TLS; the selected AAN
extension carries stock AAN records inside TLS and reconstructs logical 9P from
their payloads. It does not change the afid proof format. TLS completes before any
AAN record or 9P request. Disable early application data. A configured
server identity maps a `server` ID to an expected DNS identity and one or more
explicitly pinned SHA-256 SubjectPublicKeyInfo digests. Verify the certificate
identity, validity period and pin; pins are provisioned out of band, never learned
from the authentication challenge. No plaintext or weaker-profile fallback.

The user listener does not require a TLS client certificate: the factotum proof
authenticates the user. This is a new explicit transport profile, not a weakening
of the mutually authenticated internal-node profile in the transport specs.
TLS terminates at the host that verifies the proof, not an untrusted forwarding
proxy. Bootstrap server pins and local node credentials do not require grains.

V1 binds proofs to the authenticated server certificate plus server-side session
state. This is endpoint binding, not the RFC 9266 TLS-exporter channel binding.
It does not claim protection after an enrolled server's TLS private key or trusted
host is compromised. Different enrolled nodes must not share TLS private keys.
An exporter-based profile can be separately specified later; silently claiming a
certificate hash is a unique TLS exporter is forbidden.

## Enrollment

An operator enrolls `(authdom, user, key_id, algorithm, public_key)` out of band.
`algorithm` is exactly `libtab-eddsa-blake2b-v1`; its 32-byte public key verifies
Monocypher `crypto_eddsa_*` signatures, not RFC 8032 Ed25519/SHA-512 signatures.
Key bytes use canonical padded base64url. A principal may have several active
keys during rotation. A key ID is unique within the policy bundle and never
reassigned to different key bytes. The same public key cannot identify two
different principals. Disabled principals and disabled keys cannot authenticate.

User, domain, server and key identifiers are 1..64 ASCII characters from
`A-Z a-z 0-9 . _ -`, excluding the complete names `.` and `..`. No case folding,
Unicode normalization, domain suffix inference or authentication by display name.
The listener has one configured auth domain. `Tauth.uname` is the enrolled user;
`Tauth.aname` is exactly `fog` for logical job/resource access. The explicitly
configured [AAN bootstrap profile](../fog-v1-profiles/AanSession.md) additionally
permits `aan`, signed as that exact distinct export and usable only for bootstrap.
Numeric attach identities do not substitute for this proof. V1's acceptance baseline
does not require other 9P dialects.

## Authentication-fid exchange

1. `Tauth` reserves an unused connection-local fid and returns `Rauth` with a
   `QTAUTH` Qid. A used fid is rejected without disturbing its existing owner.
   The server retains the requested user/export, policy generation and a monotonic
   expiry. Pre-auth limits apply even to unknown users.
2. Ordinary `Tread` operations on that afid return a pinned canonical LibTab
   challenge with byte offsets. No `Topen` is required. Positive reads return
   available challenge bytes, EOF only at its end; zero reads consume nothing.
3. The client validates the complete challenge against local configuration and
   the actual TLS connection, then asks its local factotum to sign its exact bytes.
4. `Twrite` operations upload one proof document to the same afid. Successful
   completion authenticates the afid, not a grain and not an arbitrary export.
5. `Tattach` supplies that afid and exactly the same `uname` and `aname` to obtain
   the authorized root. `NOFID`, incomplete authentication or a different user or
   export is denied on the user listener. The principal comes from verification,
   not from the raw attach string.

The same verified afid can authorize repeated attaches for that user/export on
that connection while valid, as in Plan 9. Each root fid gets an independent
namespace group by default. Challenge consumption is single-use proof verification,
not a prohibition on repeated authorized attaches. Clunking the afid prevents new
attaches through it but does not alone revoke roots already attached through it.
The challenge deadline applies until verification commits; the verified afid then
shares the connection's fixed maximum session lifetime and policy epoch. Neither
a new attach nor another authentication attempt resets that connection lifetime.
Identity is attached to each verified afid/root, not to an arbitrary User field on
the whole multiplexed connection.

Version reset, terminal session loss or host restart invalidates every old afid. A new
authentication conversation gets fresh random identifiers even if a fid number
is reused. No authentication or open-handle state migrates to another logical session.
A direct connection terminates on disconnect. The proposed
[AAN extension](../swarm-9p-transport/Aan.md) distinguishes physical carrier loss
from logical-session loss; its authenticated resume binding is defined by fog-aan-v1
and must be verified before retaining authority over a replacement carrier. This afid proof is not a
resume credential, and suspension cannot extend any authentication deadline.

## Exact documents and local signing

The challenge is one `fogauth-v1` schema and one explicit row, in this column order:

| Column | Meaning |
| --- | --- |
| `server` | Configured server ID, checked by the client |
| `authdom` | Listener authentication domain, checked by the client |
| `user` | Requested enrolled principal |
| `aname` | Exactly `fog` |
| `session` | 32 fresh CSPRNG bytes, lowercase hexadecimal, unique to this 9P session |
| `afid` | Unsigned decimal authentication-fid number |
| `nonce` | 32 fresh CSPRNG bytes, lowercase hexadecimal, unique to this attempt |
| `server_cert_sha256` | Lowercase hexadecimal SHA-256 of the actual TLS leaf certificate DER bytes |
| `policy_epoch` | Active positive policy generation |
| `ttl_ms` | Configured positive challenge lifetime, informational to the client |

The session and nonce are independent. The server measures expiry using its own
monotonic timer starting at Tauth, not a client-supplied date or timer reset by IO.
The client computes the certificate digest from its own TLS peer certificate,
checks all identity/export fields, validates the strict schema, and only then
requests signing. It must not pass arbitrary server-provided bytes blindly to
factotum. It chooses a locally approved key for this service and principal.

Existing local factotum conversation:

```text
start proto=monocypher role=sigcell user=alice
write <exact validated fogauth-v1 document bytes>
read
```

The angle-bracket line describes a binary-length RPC payload, not text to send
literally. Production key selection must also constrain the enrolled key ID/public
key and service through approved factotum attributes; `user=alice` alone is merely
illustrative. Respect `needkey`/confirmation/vault errors; do not fall back to
extracting a seed. The local socket must be protected by the user's OS permissions.
No request to the fog opens or exports the user's factotum namespace.

The result cell is carried as the `proof` field of one `fogproof-v1` row, with
columns `key_id`, `proof`, in that order; `proof` is declared `SIGNED`. The key ID
only selects an already enrolled verification key for the requested principal.
Its label confers no authority. The signed body must equal the exact retained
challenge bytes; verify the signature over those bytes, not over reparsed or
reserialized input. The server never accepts a public key supplied in the proof.
The `fogauth-v1` body provides protocol separation; a `tpm9p-user-auth-v1` proof
is not accepted even if its signature and user/domain are valid.

Documents use the same strict UTF-8, LF, nil/entity and duplicate-field checks as
the job contract. Canonical files end in a single empty line. Reject unknown
columns, extra schemas/rows, raw NUL, and noncanonical encodings before verification.
Reject duplicate rows before LibTab deduplication. Challenge size is at most 2048
bytes; proof document at most 4096 bytes. The resulting signed cell fits the existing
4096-byte libauth RPC reply buffer including RPC status overhead. Actual factotum
integration must test this bound rather than assuming arbitrary-size signing works.

Proof writes use contiguous offsets starting at zero. Split UTF-8 sequences may be
buffered within the total byte bound; decode strictly after document completion.
The schema/row empty-line separators identify the end of the one-row document;
the final empty line seals it. Extra bytes in that Twrite or any later proof write
are rejected. Intermediate Rwrites acknowledge only buffered bytes. The final
Rwrite succeeds only after validation, signature verification, current enrollment
check and atomic challenge consumption succeed. Failed or malformed completed
proofs invalidate the attempt; retry uses a new Tauth. Offsets, gaps, overlaps and
overflow are checked before mutation. Treads continue to return the same challenge,
not a changing status stream.

A lost final Rwrite can leave authentication uncertain. Try the known afid in
Tattach on the same connection; do not replay a potentially accepted proof write.
Tflush observes the usual reply barrier. A flush that wins before verification
commit invalidates the attempt; one after commit does not undo authentication.
The commit and flush winner is serialized per afid. No reply follows its Rflush.

## Limits, failures and remaining server policy

Configure finite connection, afid, outstanding-authentication, buffered-proof,
signature-check concurrency, attempt-rate and authentication-time limits. Fair
admission and per-source limits precede expensive verification. Unknown users,
unknown keys and bad signatures return the same bounded `auth-failed` error;
logs may distinguish causes for operators without storing proofs or secrets.
Abandoned attempts are reclaimed without a client clunk.

Authenticated sessions have a finite maximum lifetime that IO does not extend.
Expiry denies further operations and tears down handles, but does not cancel an
already admitted job merely because its submitting client expired. Explicit policy
revocation has the additional job behavior in NamespacePolicy.md. New authentication
is required for a replacement logical session; the owner's retained job ID remains
usable if policy still permits it. AAN cannot turn expired authentication into a
resumable session or preserve handles across explicit revocation.

Public-key enrollment, proof verification and access grants are distinct. Successful
authentication alone never grants node membership, `/transport/orleans`, provider
installation, unrestricted namespace changes, or access to another user's jobs.

## Sources and compatibility review

- [Plan 9 auth and attach](https://9p.io/magic/man2html/5/attach): afid exchange and
  reuse for matching attaches. This profile defines its payload, not new 9P messages.
- `../9front/sys/src/lib9p/auth.c`: lifecycle reference, not a drop-in driver for
  signed cells; it expects p9any/AuthInfo protocol completion.
- `../factotum-dp9ik/src/cmd/auth/factotum/{monocypherproto,monokey,tpm9p}.c`:
  actual signing algorithm, signed-cell encoding and existing domain-separated proof.
- [Monocypher signatures](https://monocypher.org/manual/eddsa): BLAKE2b EdDSA is
  distinct from Ed25519. Cross-language vectors must exercise the exact algorithm.

The protected TLS connection supplies server authentication and confidentiality;
the signature proves possession of an enrolled user key. No TPM backing is implied
by the name `tpm9p`; hardware-bound keys need their own explicitly supported profile.
