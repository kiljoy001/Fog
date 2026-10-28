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

  @FOG_N09
  Scenario Outline: A guest never inherits host infrastructure authority
    Given a sandboxed guest application with permission for its own files only
    When it attempts to use <resource>
    Then the host boundary denies access without an external effect

    Examples:
      | resource                   |
      | the user's factotum        |
      | node TLS private keys      |
      | /transport/orleans         |
      | /control                   |
      | /admin/ctl                 |

  @FOG_N11 @property
  Scenario: Revocation and resource acceptance have an explicit winner
    Given an old-scope mutation and local policy revocation held at competing barriers
    When the barriers are released in generated orders
    Then a mutation accepted before revocation may finish without a rollback claim
    And a mutation reaching acceptance after revocation is denied without an effect
