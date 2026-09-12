@fog_v1_profiles
Feature: Control services share bounded atomic transaction files
  Uploading a record is not committing its requested operation.

  @FOG_V1_TX01 @property @wire
  Scenario: Fragmented input has one frozen commit boundary
    Given a transaction with a valid request and its required auxiliary files
    When the client uploads them at generated contiguous fragment boundaries
    Then no requested operation executes before all files are sealed and commit is accepted
    When two commit requests race for that transaction
    Then exactly one frozen fixture operation executes and both observe its retained outcome

  @FOG_V1_TX02 @fuzz
  Scenario Outline: Malformed staging cannot become a partial valid request
    Given a staging transaction with a known sealed revision
    When its replacement contains <defect>
    Then validation rejects it before any requested effect
    And resource ownership remains within the configured limits

    Examples:
      | defect                        |
      | duplicate rows before deduplication |
      | an unknown column             |
      | invalid UTF-8                 |
      | a gap or overlapping write    |
      | an overflowing byte count     |
      | an encoded cell above 7168 bytes |
      | an unsealed required file     |

  @FOG_V1_TX03
  Scenario: Lost commit replies are resolved under the known transaction identity
    Given a transaction whose effect and successful outcome have committed
    When its reply is lost and its owner repeats commit under the same ID
    Then the retained outcome is returned without another effect
    And attempts to replace its frozen request are rejected

  @FOG_V1_TX04
  Scenario: Expiring retry evidence never recreates an operation
    Given a committed transaction whose retained outcome has expired
    When a client walks or commits its old ID
    Then it receives absence or tx-expired without an allocation or effect
    And only a new clone open can allocate a new staging identity

  @FOG_V1_TX05 @property
  Scenario: Snapshot files describe one revision
    Given a completed read transaction with multiple result files
    When the underlying service changes while those files are read at varied offsets
    Then all reads still describe the transaction's original atomic snapshot
    And an oversized snapshot fails rather than dropping rows

  @FOG_V1_TX06 @security
  Scenario: Retention does not preserve revoked access
    Given a completed private transaction owned by an authenticated principal
    When that principal loses the authority required to inspect the result
    Then a retained ID cannot be used to read or recommit it
    And another principal cannot access it by guessing its pathname

  @FOG_V1_TX07
  Scenario: Capacity is reserved before a requested effect
    Given the transaction or result capacity needed by an operation is exhausted
    When the client commits its valid request
    Then the service rejects admission before applying the effect
    And control cleanup does not require a spare application data slot

  @FOG_V1_TX08 @wire
  Scenario: Flushing a commit orders replies without promising rollback
    Given a committing transaction racing with Tflush
    When the fixture releases the commit and flush barriers in either order
    Then no old reply follows Rflush
    And the recorded effect and outcome remain atomic
    And the client can resolve an accepted commit through its known transaction ID
