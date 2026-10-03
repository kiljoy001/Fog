# Authentication

Status: implemented in `NinePSharp.Fog.Auth`, `NinePSharp.Fog.Namespaces` and
`NinePSharp.Fog.Server`, with executable scenarios and an interoperability test against a
stock 9front terminal. Not yet security-reviewed.

Fog is the authentication server of its own auth domain. It keeps users' keys as 9front's
keyfs(4) does, issues tickets as 9front's authsrv (auth(8), protocol in authsrv(6)) does with `-N`, and authenticates 9P
attaches with dp9ik on the afid as 9front's lib9p does with factotum(4). A 9front terminal
logs in with its own factotum, `srv`, `mount` and `passwd`, unchanged. The 9front sources at
`../9front` (`front` branch) define every wire format, file format and error string here
unless this document says otherwise.

This replaces the earlier `fog-auth-v1` profile, which proposed signed LibTab challenges
over TLS. That profile was never implemented.

## Pieces

| Piece | 9front counterpart | Implementation |
| --- | --- | --- |
| Key database | keyfs(4), `/adm/keys` | `KeyFsHost`, `KeyDatabase`, `KeyFsStore`, `StorageKeySeal` |
| Ticket service | authsrv `-N`, auth(8) | `AuthServerHost`, `AuthServerConnection` |
| Password change | passwd(1) against authsrv | `AuthServerConnection` (AuthPass) |
| Afid authentication | lib9p `auth9p`, `authread`, `authwrite`, `authattach` with factotum's p9any/dp9ik server role | `AuthFidExport`, `P9anyServer` |
| User attach | file server attach as the ticket's user | `FogNamespaceAttachResolver` |
| User listener | a file server on `tcp!*!564` | `FogUserListener` |

The dp9ik primitives (AuthPAK, form 1 tickets and authenticators, password requests) come
from the `Dp9ik` package (`../dp9ik.net`), which is checked against drawterm's C code.

## The key database

The database holds one record per user in keyfs's 89-byte layout: a NUL-terminated name of
at most 27 bytes, the DES key, status, warnings, expiry, secret and AES key. A user is
read and changed through keyfs's file tree, `/{user}/{key,aeskey,pakhash,secret,log,status,expire,warnings}`,
with keyfs's semantics: disabled and expired users, the bad-attempt log with purgatory at
every tenth failure, and keyfs's error strings.

The tree is served only on a Unix-domain socket, mode 0600, in the keyfs state directory.
Whoever can open that socket administers every user, so the host account that runs Fog is
the administrator. There is no remote administration.

On disk the records are sealed differently from 9front, whose keyfile is AES-CBC without
integrity:

- The file is `FOGKEYS1`, a 12-byte nonce, and the records under ChaCha20-Poly1305 with
  a 32-byte storage key, the magic as associated data. Any change to the file is detected.
- The storage key is sealed to the host's TPM as a keyed-hash object under a transient
  ECC P-256 primary in the owner hierarchy. No persistent TPM handle is used.
- There is no PCR policy. The aim is to keep the key off the disk, not to detect host
  tampering: if the host is compromised, so is Fog.
- At creation keyfs gives out the storage key once as a 24-word BIP-39 phrase. The phrase
  recovers the database on another TPM, and is checked against the database before
  anything is sealed.

## The ticket service

`AuthServerHost` serves authsrv's ticket protocol over TCP for the keys keyfs holds:

- **AuthPAK** runs the PAK exchanges for the request's authid and hostid, or for the uid
  alone, filling the slots authsrv calls akey, hkey and ukey. Each PAK key serves one
  request.
- **AuthTreq** returns an AuthTc and an AuthTs ticket, both form 1, under the PAK keys.
  DES is disabled, as with `-N`. A request whose keys have no PAK key is refused with
  "DES is disabled".
- **Speaks-for.** A host may ask for tickets for another user only as the configured
  speaks-for rules allow. They have `/lib/ndb/auth`'s meaning, including `*` and `!user`.
- **Unusable users.** An unknown, disabled, expired or purgatory user gets a random key,
  so the client receives a ticket that nobody can open, as authsrv's `mkkey` does.
- **Lifetime.** A connection's whole lifetime is bounded, as authsrv's alarm bounds it
  (10 minutes by default).
- **AuthPass**, after a PAK exchange for the uid, sends an AuthTp ticket and accepts
  password requests until one succeeds.
  - Each request is checked as `changepasswd` and `okpasswd` check it: at least 8
    characters after trailing spaces are removed, and not trivial forwards or backwards.
  - A refusal names the client's address.
  - Success clears the user's bad attempts.

The service listens where it is configured. A 9front client finds it through ndb's
`authdom=… auth=…` entry. The `auth` value may be a full dial string such as
`tcp!host!port`, since `authdial` leaves a complete address unchanged.

## Afid authentication

`AuthFidExport` wraps a 9P export. It is configured with an auth id (the keyfs user whose
key the server holds, as a 9front file server's factotum holds its hostowner's key) and an
auth domain.

1. **Tauth** opens an afid with a QTAUTH qid, from the connection's fid space. A fid in use
   is a "duplicate fid". An empty uname is refused.
2. **Reads and writes on the afid** speak factotum's p9any server role:
   - The offer is `dp9ik@{authdom}` followed by a NUL, without p9any's `v.2` prefix, as a
     9front server sends it. p9sk1 is never offered.
   - The dp9ik exchange is p9sk1.c's server role: the client's challenge, an AuthPAK
     ticket request with the server's PAK value, the PAK value from the auth server, the
     AuthTs ticket with an AuthAc authenticator, and the server's AuthAs authenticator.
     The ticket must be form 1 and the challenges must match.
   - As in factotum, a failed write leaves the exchange where it was, so the client can
     write again.
3. **Errors follow lib9p.**
   - A read the count cannot hold is still consumed and fails with "authread count too
     small".
   - Every failed read is "authrpc botch".
   - A write in the wrong phase is "phase error …" with factotum's phase name.
   - Where lib9p would report a stale errstr, Fog says what happened: "no uname", "rpc too
     small", "authentication already done".
4. **Tattach** with an afid follows `authattach`:
   - "unknown fid", "not an auth fid", "auth uname mismatch: … vs …" and "auth aname
     mismatch: … vs …".
   - An afid whose exchange is unfinished fails as a zero-count `authread`.
   - A finished afid whose ticket names another client user fails with "auth uname
     mismatch".
5. **An authenticated afid** serves any number of matching attaches on its connection
   until it is clunked or removed.
   - Tversion and closing the connection end every afid.
   - An afid is not a file: walk, open, create, stat, wstat and remove fail as lib9p's do
     for a server without those operations, and remove also clunks it.
   - Afids count against the connection's fid limit.

An attach without an afid passes to the export underneath unchanged.

## Who attaches

`FogNamespaceAttachResolver` resolves two kinds of attach to a copy of the shared root, the
one namespace every principal shares.

| Attach | Principal | Transport |
| --- | --- | --- |
| With an afid | The client user of the afid's ticket | `FogUserListener`: plain 9P, as a 9front file server listens |
| Without an afid | The enrolled node named by its certificate | `FogNodeListener`: TLS 1.3 with client certificates |

The attach name must be `/` or empty. 9front's `srv` and `mount` attach with an empty name.

The principal is what the authorization policy grants to; see
[NamespacePolicy.md](NamespacePolicy.md). Users and nodes share one name space of
principals, as Plan 9 has one name space of users, so an operator must not give a keyfs
user the name of an enrolled node.

dp9ik authenticates both ends and gives each a session secret. The user listener does not
use that secret to encrypt, exactly as a 9front file server on port 564 does not. Traffic
between a user and Fog is therefore readable on the network. A confidential user transport
(9front's `tlssrv`/`tlsclient`, or AAN inside TLS) is future work and must be negotiated
explicitly, never by fallback.

## Names

A keyfs user name is 1 to 27 bytes of UTF-8 (ANAMELEN − 1, for parity with 9front tickets).
It has no space, `/`, control character or invalid UTF-8, and is not `.` or `..`.
Comparison is exact: no case folding or normalization.

Policy identifiers have their own grammar: 1 to 64 ASCII characters from
`A-Z a-z 0-9 . _ -`, excluding `.` and `..`. A user who is to be granted anything must have
a name that satisfies both.

## Unfinished work

Nothing waits without a bound. Closing a session, Tversion and Tflush cancel in-flight
requests and wait at most the drain limit (5 seconds by default).

- **Abandoned requests.** A request still running after the drain limit is abandoned. It is
  answered with Rerror `unknown` and logged with an unknown outcome, because its effect
  may still happen; flush(5) asks the same of a client whose flushed request had no reply.
- **Connections.** A connection that has ended releases its socket and admission slot
  within the drain limit.
- **Listeners.** A listener being disposed force-closes connections still open after twice
  the drain limit.

## Verification

Executable scenarios:

| Feature | Covers |
| --- | --- |
| `NinePSharp.Fog.Auth.Tests/Features/KeyFs.feature`, `KeyFsProtocol.feature` (`@FOG_KEYFS_`) | Records, files, sealing, recovery, the admin socket, 9P behaviour |
| `AuthSrv.feature` (`@FOG_AUTHSRV_`) | PAK exchanges, tickets, speaks-for, unusable users, lifetime |
| `AuthPass.feature` (`@FOG_AUTHPASS_`) | Password and secret changes, `okpasswd`, refusals, retries |
| `AuthFid.feature` (`@FOG_AUTHFID_`) | p9any/dp9ik on the afid, lib9p's afid rules and errors |
| `NinePSharp.Fog.Namespaces.Tests/Features/NamespaceViews.feature` (`@FOG_VIEW_012`, `@FOG_VIEW_013`) | User attach, empty attach name |
| `NinePSharp.Fog.Server.Tests/Features/Draining.feature` (`@FOG_DRAIN_`) | Bounded drains and unknown outcomes |
| `NinePSharp.Fog.Namespaces.Tests/Features/NineFrontInterop.feature` (`@FOG_INTEROP_`) | A stock 9front terminal in QEMU: `srv` and mount with dp9ik, grants, writes, a wrong password retried through factotum, `passwd` |

The interoperability scenarios run when `FOG_9FRONT_ISO` names a 9front amd64 ISO and
`qemu-system-x86_64` is installed. The ISO is changed only by rewriting `plan9.ini` for a
serial console.

Each auth project has a 100% Stryker mutation gate (`stryker-config-fog-auth.json`, and
the configs for Fog.Namespaces, Fog.Server, the Orleans server and the transport), with
timeouts treated as findings.

## Not yet done

- A security review of the whole design.
- Secstore, and running these services in the WASM runtime.
- A confidential user transport, and binding AAN resumption for users to dp9ik. The AAN
  documents still describe the retired `fog-auth-v1` binding.

## Sources

- `../9front/sys/src/cmd/auth/keyfs.c`, `authsrv.c`, `passwd.c`, `lib/okpasswd.c`
- `../9front/sys/src/cmd/auth/factotum/p9any.c`, `p9sk1.c`, `rpc.c`
- `../9front/sys/src/lib9p/auth.c`, `srv.c`
- `../9front/sys/src/libauthsrv/authdial.c`, `convM2T.c`, `convM2A.c`, `form1.c`
- `../9front/sys/src/cmd/srv.c`; manual pages keyfs(4), auth(8), authsrv(6), factotum(4),
  passwd(1), ndb(6), attach(5), flush(5)
