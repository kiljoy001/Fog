# Namespace verification

## Fog shared-root namespace export — 2026-10-01

`NinePSharp.Fog.Namespaces` serves each enrolled node its own namespace over 9P. A host's
`FogSharedRoot` is a durable root directory grain (`/`, `/mnt`, `/bin`, `/n`, unwritable
placeholders per namespace(4)) plus a process-group grain; applications are grains mounted at
`/mnt/{app}`. `FogNamespaceAttachResolver` takes the principal only from the node certificate
(`FogNodePolicy`), copies the shared group for each attach and gives the session the node's
`AuthorizedResourceOperations` view. `SharedRootAncestry` extends containment across the shared
root's operator mounts only, so a node's own binds never widen its grants. `BoundedNamespaceExport`
applies the control export's session, fid, request, message-size and lifetime bounds.
`DistributedNamespaceAttach.Resources`, `DistributedNamespaceOperations.WithResources` and
`IAncestryResourceGrain` were added to the Orleans layer; `FogNodeListener` accepts any dispatcher.

- Features: `docs/specifications/fog-foundation/NamespaceViews.feature` (design) and the
  executable `NinePSharp.Fog.Namespaces.Tests/Features/NamespaceViews.feature`, run against a
  two-silo Orleans test cluster with real certificates and, for FOG_VIEW_011, the TLS listener.
- Tests: 38 passed in `NinePSharp.Fog.Namespaces.Tests`; Orleans 148, Fog server 151,
  authorization 96 and namespace 478 unchanged and passing.
- Coverage: 100% lines and methods, 99.5% branches (the remainder is a `lock` statement's
  compiler-generated branch).
- Stryker: `stryker-config-fog-namespaces.json` 100% (196 killed, 0 timeouts, 0 survivors);
  Orleans 100% (118) and Orleans server 100% (172) after the changes. No exclusions. The first run
  found 62% and an off-by-one hazard: had path numbers been allocated downward, the first entry
  after the root would get number 0, the "no parent" sentinel.
- `bash scripts/run-quality.sh`: passed; merged coverage 86.06% lines, 77.77% branches.

Not yet done: `/n/{name}` resolution and import, installation by writing `/bin/{app}`, each
application's own namespace, and resource-side (grain) authorization checks.
