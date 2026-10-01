@fog_namespace_views
Feature: Enrolled nodes attach to their own copy of the shared namespace
  The host's shared root lays out /mnt, /bin and /n as Plan 9 namespace(4) describes.
  Applications are grains mounted at /mnt/{app}. Each attach owns a process-group grain
  copied from the shared root, and every operation goes through the node's authorization
  view. Requests are 9P messages sent through the Fog namespace export with the node's
  certificate, exactly as the TLS listener delivers them.

  Background:
    Given a shared root with applications "mail" and "notes" mounted under /mnt
    And the shared root also contains "/admin/ctl"
    And enrolled nodes "worker" and "other"
    And group "nodes" has members "worker, other"
    And the policy generation is 1 with grants
      | kind  | subject | path       | scope | rights                     |
      | group | nodes   | /          | self  | stat,walk,read             |
      | group | nodes   | /mnt       | self  | stat,walk,read             |
      | user  | worker  | /mnt/mail  | tree  | stat,walk,read,write       |
      | user  | other   | /mnt/notes | tree  | stat,walk,read             |

  @FOG_VIEW_001
  Scenario: Each attach owns a namespace copied from the shared root
    When "worker" attaches
    And "other" attaches
    Then the two attaches own distinct process groups
    And each process group mounts the same applications as the shared root

  @FOG_VIEW_002
  Scenario: A node's mount changes stay its own
    Given "worker" and "other" have attached
    When "worker" mounts "/mnt/notes" on "/mnt/mail" in its own namespace
    Then the shared root still mounts "mail" at "/mnt/mail"
    And the namespace of "other" still mounts "mail" at "/mnt/mail"

  @FOG_VIEW_002
  Scenario: A node's own bind confers no authority
    Given "worker" has attached
    When "worker" reads "/mnt/mail/inbox"
    Then the read returns "hello mail"
    When "worker" mounts "/mnt/notes" on "/mnt/mail" in its own namespace
    And "worker" walks to "/mnt/mail/todo"
    Then the walk reports not found

  @FOG_VIEW_003
  Scenario: Authorization shapes what each node sees under /mnt
    Given "worker" and "other" have attached
    When "worker" lists "/mnt"
    Then the listing contains exactly "mail"
    When "other" lists "/mnt"
    Then the listing contains exactly "notes"
    When "other" walks to "/mnt/mail/inbox"
    Then the walk reports not found

  @FOG_VIEW_004
  Scenario: Dot-dot is clamped at the root
    Given "worker" has attached
    When "worker" walks "..", "..", ".." from its root
    Then the walk ends at the shared root's directory

  @FOG_VIEW_005
  Scenario: A granted node reads and writes an application's files
    Given "worker" has attached
    When "worker" writes "new mail" to "/mnt/mail/inbox"
    Then the mail application recorded one write
    And "worker" reads "/mnt/mail/inbox" as "new mail"

  @FOG_VIEW_005
  Scenario: A read-only grant cannot write
    Given "other" has attached
    When "other" writes "x" to "/mnt/notes/todo"
    Then the request fails with "permission denied"
    And the notes application recorded no write

  @FOG_VIEW_006
  Scenario Outline: Ungranted infrastructure is absent
    Given "worker" has attached
    When "worker" walks to "<path>"
    Then the walk reports not found

    Examples:
      | path               |
      | /admin/ctl         |
      | /transport/orleans |

  @FOG_VIEW_007
  Scenario Outline: Identity comes only from the authenticated certificate
    When the certificate of "<certificate>" attaches as "<uname>"
    Then the attach fails

    Examples:
      | certificate | uname    |
      | worker      | other    |
      | worker      | stranger |
      | stranger    | worker   |

  @FOG_VIEW_008
  Scenario: Reading a directory over 9P returns only authorized entries
    Given "other" has attached
    When "other" opens "/mnt" and reads its stat records
    Then the records name exactly "notes"

  @FOG_VIEW_009
  Scenario: Replacing the policy invalidates fids from the old generation
    Given "worker" has attached
    And "worker" has opened "/mnt/mail/inbox" for reading
    When the host replaces the policy with generation 2 and the same grants
    Then reading through that fid fails with "permission denied"
    When "worker" attaches again
    Then "worker" reads "/mnt/mail/inbox" as "hello mail"

  @FOG_VIEW_010
  Scenario: The session limit bounds attaches
    Given the export admits at most 1 session
    And "worker" has attached
    When "other" negotiates a version
    Then the request fails with "limit"

  @FOG_VIEW_010
  Scenario: The fid limit bounds walks
    Given the export admits at most 2 fids per session
    And "worker" has attached
    When "worker" walks to "/mnt" into a new fid
    And "worker" walks to "/mnt/mail" into another new fid
    Then the request fails with "limit"

  @FOG_VIEW_010
  Scenario: The session lifetime bounds every request
    Given "worker" has attached
    When the session lifetime elapses
    And "worker" walks to "/mnt"
    Then the request fails with "denied"

  @FOG_VIEW_010
  Scenario: Requests larger than the negotiated message size are refused
    Given "worker" has attached with message size 256
    When "worker" writes 300 bytes to "/mnt/mail/inbox"
    Then the request fails with "invalid-request"

  @FOG_VIEW_011
  Scenario: The export is served over the enrolled-node TLS listener
    Given the export behind the TLS node listener
    When "worker" connects with its certificate and reads "/mnt/mail/inbox"
    Then the read returns "hello mail"
    And a connection without an enrolled certificate is rejected before any 9P request
