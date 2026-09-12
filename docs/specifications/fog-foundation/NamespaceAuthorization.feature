@fog_foundation @security
Feature: Authenticated namespaces carry bounded resource authority
  A visible path, known grain ID or claimed User string is not authorization.

  @FOG_N01
  Scenario: An attach constructs only the configured principal view
    Given two enrolled principals with different namespace profiles
    When each authenticates and attaches to the fog export
    Then each root contains only its configured and permitted resource projections
    And the roots use distinct process-group ownership
    And no client-supplied group or process ID selects another principal's view

  @FOG_N02 @property
  Scenario Outline: Alternate access paths cannot bypass effective permissions
    Given a principal without write authority to a protected resource
    When it attempts access through <route>
    Then the resource host denies the mutation before an external effect

    Examples:
      | route                                |
      | a guessed direct pathname            |
      | an alias to the same stable object   |
      | a later member of a union mount      |
      | traversal above the exported root    |
      | a forged resource identity           |
      | a serialized context claiming another User |

  @FOG_N03 @property
  Scenario: Read-only projection attenuates a writable underlying resource
    Given an otherwise writable resource exported through a read-only mount
    When generated write, truncate, create, remove and ctl-write requests use that view
    Then every mutation is denied before resource dispatch
    And permitted reads preserve the same stable object identity

  @FOG_N04
  Scenario: Groups require actual membership and all permission layers
    Given a resource grant to a group and a principal who is not its member
    When that principal opens the resource using a matching group-like username
    Then access is denied
    When an actual member opens it without the required server mode permission
    Then access is also denied

  @FOG_N05
  Scenario: Ordinary mode changes preserve an open grant but not new access
    Given a principal has opened a resource with valid read authority
    When its ordinary file mode changes to deny a new read open
    Then the existing read handle remains usable within its scope and lifetime
    And a new read open is denied
    And this does not suppress explicit authority-epoch revocation

  @FOG_N06
  Scenario: Moving an open object does not authorize its new neighbors
    Given a read handle admitted under a verified tree grant
    When the object moves outside that tree and the principal retains its handle
    Then the handle can still read only its originally authorized object
    And it cannot be converted into a grant over the new parent or siblings

  @FOG_N07
  Scenario: A job freezes and attenuates its submitting namespace
    Given an admitted job with pinned source and a read-only artifact grant
    When the submitting client's mount table changes
    Then the executing job retains its admitted namespace and artifact identities
    And it cannot obtain a newly mounted resource or writable input handle
    And preparing the job cannot perform externally visible namespace writes

  @FOG_N08
  Scenario: Provider permission is separate from control-file access
    Given an owner who can write its staging job ctl but cannot execute the selected provider
    When it writes start
    Then admission fails without starting that provider
    And neither a known provider version nor a guessed job ID grants the missing right

  @FOG_N09
  Scenario Outline: A guest never inherits host infrastructure authority
    Given a sandboxed job with permission for its input and bounded result only
    When it attempts to use <resource>
    Then the host boundary denies access without an external effect

    Examples:
      | resource                   |
      | the user's factotum        |
      | node TLS private keys      |
      | /transport/orleans         |
      | /control                   |
      | /admin/ctl                 |
      | /compute/clone             |
      | another owner's retained result |

  @FOG_N10 @cluster
  Scenario: A remote resource validates scope rather than trusting node reachability
    Given a job assigned to one enrolled worker with a registered scope
    When an internal request names the wrong job, worker incarnation or expired scope
    Then the resource host rejects it even though the node TLS channel is authenticated
    And accepting a valid request never reveals a reusable user login proof

  @FOG_N11 @property
  Scenario: Revocation and resource acceptance have an explicit winner
    Given an old-scope mutation and local policy revocation held at competing barriers
    When the barriers are released in generated orders
    Then a mutation accepted before revocation may finish without a rollback claim
    And a mutation reaching acceptance after revocation is denied without an effect
