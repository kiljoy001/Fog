@fog_v1_profiles @cluster
Feature: The pinned Orleans membership API has atomic 9P records
  Membership metadata does not authorize worker execution or bypass transport framing.

  @FOG_V1_MEM01
  Scenario: Initialization is idempotent and boot-bound
    Given a control host with a configured cluster and no running grains
    When multiple adapters initialize the membership table
    Then they observe the same initialized table version and control boot
    And a noncreating initialization never creates a missing table
    And no native Orleans or database connection is opened

  @FOG_V1_MEM02 @property
  Scenario: Insert races atomically change a row and table version
    Given two inserts naming the same expected table etag and next version
    When their commit barriers race
    Then exactly one returns true and the other returns false for conflict
    And the winning row, votes and row and table etags are committed together
    And the losing insert leaves no partial row or version change

  @FOG_V1_MEM03 @property
  Scenario Outline: Update checks every optimistic-concurrency precondition
    Given a membership update with <defect>
    When the adapter commits it
    Then it returns false for conflict without changing row data, votes or table version

    Examples:
      | defect                   |
      | a missing row            |
      | a stale row etag         |
      | a stale table etag       |
      | a nonconsecutive next version |

  @FOG_V1_MEM04 @property
  Scenario: ReadRow and ReadAll cannot combine different revisions
    Given concurrent membership and suspect-list updates
    When the adapter reads a row and then a whole-table snapshot through separate transactions
    Then each transaction individually contains coherent entries, votes and table version
    And an absent row has zero entries rather than a fabricated member

  @FOG_V1_MEM05
  Scenario: IAmAlive is a self-bound update without table version mutation
    Given an existing membership row and its etags and suspect votes
    When its owner sends a new alive time and later an older delayed alive time
    Then alive time never moves backward
    And status, votes, row etag and table version remain unchanged
    And an alive update for a removed address cannot recreate it

  @FOG_V1_MEM06
  Scenario: Defunct cleanup preserves recent death evidence and live members
    Given old Dead rows, a recently suspected Dead row and an old Active row
    When cleanup uses a cutoff newer than only the old Dead rows' effective times
    Then only those old Dead rows and their votes are removed
    And the table version advances once for the atomic change

  @FOG_V1_MEM07 @security
  Scenario Outline: Table identities do not authorize unrelated mutations
    Given an enrolled caller attempting <operation>
    When the membership transaction commits
    Then authorization rejects it before changing the table

    Examples:
      | operation                                      |
      | inserting an unconfigured endpoint             |
      | inserting under another node's identity        |
      | changing a row's registered node boot           |
      | deleting another cluster's entries             |
      | deleting all entries while the control host is active |

  @FOG_V1_MEM08 @wire
  Scenario: Gateway discovery routes only to configured 9P endpoints
    Given a snapshot containing Active, Dead and nongateway members
    When the real gateway provider returns and refreshes its gateway URIs
    Then only Active configured nonzero-proxy members are returned
    And the connection factory routes them through TLS and configured AAN to 9P
    And expired cached gateways are not used indefinitely after refresh failure

  @FOG_V1_MEM09
  Scenario: Control restart invalidates old table identities
    Given a cached table snapshot and transactions from a preceding control boot
    When the control host restarts and adapters try those old records
    Then they fail as stale rather than merging tables or replaying updates
    And a fresh join uses the new boot and configured reconciliation barrier

  @FOG_V1_MEM10 @fuzz
  Scenario: Unsupported fields and versions fail instead of being ignored
    Given malformed membership records or an unmapped enum value
    When the adapter parses them at generated fragment boundaries
    Then it rejects them within configured row and byte limits
    And version overflow cannot wrap to an old table identity
    And adapter conformance enumerates the installed Orleans 10.3.1 interface members

  @FOG_V1_MEM11 @property
  Scenario: The wire mapping preserves every public membership field
    Given generated MembershipEntry values including RoleName, UpdateZone and FaultZone
    And host, silo and role names include both nil and empty values
    When entries and their ordered suspect lists round-trip through the 9P records
    Then every public field equals its original value
    And no undocumented field is silently dropped or replaced by a default
