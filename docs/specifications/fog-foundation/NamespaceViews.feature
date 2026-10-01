@fog_foundation @namespace_authorization @design
Feature: Every principal attaches to its own copy of the host's shared namespace
  A host has one shared root composition, laid out by Plan 9 namespace(4) convention:
  /mnt/{app} for running applications, /bin/{app} for application modules and /n/{name}
  for remote hosts. There are no per-principal mount profiles. Each attach owns a
  process-group grain copied from the shared root, and the resource authorization layer
  decides what the principal may see and use, so objects without a granted right look
  absent. Remote names (RemoteNames.feature) and installation (Applications.feature) are
  later slices. Executable bindings: NinePSharp.Fog.Namespaces.Tests/Features/NamespaceViews.feature.

  @FOG_VIEW_001 @FOG_N01
  Scenario: Each attach owns a namespace copied from the shared root
    Given a host whose shared root mounts two applications under /mnt
    When two enrolled principals authenticate and attach
    Then each attach owns a distinct process-group grain
    And both namespaces start with the shared root's mounts

  @FOG_VIEW_002
  Scenario: A principal's namespace changes stay its own
    Given two attached principals
    When one binds, mounts or unmounts in its namespace
    Then the shared root and the other principal's namespace are unchanged

  @FOG_VIEW_002 @FOG_N02
  Scenario: Grants follow the shared root's mounts, never a principal's own binds
    Given a principal granted the tree at one application's mount point but not another's
    When it reaches the granted application's files through the shared root's mount
    Then its tree grant covers those files
    When it binds the other application under the granted mount point in its own namespace
    Then that application's files remain absent to it

  @FOG_VIEW_003 @FOG_N02
  Scenario: Authorization shapes what each principal sees
    Given an application under /mnt that only one principal is granted
    When each principal lists /mnt and walks to that application
    Then the granted principal sees and reaches it
    And for the other principal it is absent from the listing and the walk reports not found

  @FOG_VIEW_004
  Scenario: Dot-dot is clamped at the root
    Given an attached principal at its root
    When it walks ".." any number of times
    Then it remains at its root

  @FOG_VIEW_005
  Scenario: A running application's files are reached under /mnt
    Given an application grain mounted at /mnt/{app} in the shared root
    When a granted principal walks, opens, reads and writes files below /mnt/{app}
    Then each operation reaches that application's grain through the principal's authorization

  @FOG_VIEW_006 @FOG_N09
  Scenario Outline: Ungranted infrastructure is absent
    Given a principal with no grant on <path>
    When it walks to <path>
    Then the walk reports not found

    Examples:
      | path               |
      | /transport/orleans |
      | /admin/ctl         |

  @FOG_VIEW_007 @FOG_N01
  Scenario: Identity comes only from the authenticated transport
    Given a principal authenticated by its enrolled certificate
    When its attach names another user, attach name or process
    Then the attach is denied or receives only its own identity's namespace and authority

  @FOG_VIEW_008
  Scenario: Directory reads never bypass authorization
    Given a provider that offers raw directory streaming
    When the principal reads a directory
    Then entries come from authorized metadata reads
    And entries without a granted right are absent

  @FOG_VIEW_009 @FOG_N11
  Scenario: Replacing the policy invalidates fids from the old generation
    Given a principal with open fids under the current policy
    When the host replaces the policy
    Then those fids are denied
    And a fresh attach uses the new policy

  @FOG_VIEW_011
  Scenario: The namespace export is served over the enrolled-node TLS listener
    Given the namespace export behind the TLS 1.3 node listener
    When an enrolled node connects with its certificate and attaches
    Then it reaches its namespace over 9P
    And a connection without an enrolled certificate is rejected before any 9P request

  @FOG_VIEW_010
  Scenario: The existing export bounds still apply
    Given the host's session, fid, outstanding-request, message-size and lifetime limits
    When a principal exceeds any of them through its namespace
    Then the request fails with the same bounded error as the control export
