@fog_v1_profiles
Feature: Optional persistent resources have conditional durable writes
  A storage transaction does not make job execution or arbitrary external effects exactly once.

  @FOG_V1_ST01 @property
  Scenario: Concurrent puts have one etag winner
    Given two put transactions with different payloads and the same expected etag
    When their commits race
    Then one commits and the other reports conflict
    And a read returns one complete winning payload with its matching hash and etag

  @FOG_V1_ST02
  Scenario Outline: Crash recovery keeps effects and outcomes atomic
    Given a durable put transaction held at <boundary>
    When the storage process is terminated and recovered
    Then its value and retry outcome are either both committed or both uncommitted
    And retry under the known ID cannot apply an already committed effect again

    Examples:
      | boundary                            |
      | before SQLite commit                |
      | during SQLite commit                |
      | after commit but before Rwrite      |
      | after Rwrite but before checkpoint  |

  @FOG_V1_ST03
  Scenario: Tombstones prevent stale absent writers from recreating cleared state
    Given a value has been cleared and has a new tombstone etag
    When one writer uses absent and another uses the tombstone etag
    Then the absent writer conflicts
    And only the writer naming the current tombstone can recreate the value
    And no old etag is reused

  @FOG_V1_ST04
  Scenario: Empty state is not cleared state
    Given an admitted put of a zero-length payload with the empty-content hash
    And its registered fixture codec explicitly supports an empty payload
    When it commits and is read through IGrainStorage
    Then RecordExists is true and the codec receives the empty payload
    And this outcome is distinct from a clear with RecordExists false

  @FOG_V1_ST05 @security @fuzz
  Scenario Outline: Invalid storage input cannot select code or corrupt a value
    Given a storage request containing <defect>
    When it is validated and committed if eligible
    Then it fails without loading unregistered code or changing the stored value

    Examples:
      | defect                              |
      | an unregistered codec               |
      | a guest-selected CLR type name      |
      | a mismatched payload hash           |
      | a truncated payload                 |
      | an overflowing payload byte count   |
      | a wildcard expected etag            |
      | a key with invalid base64url         |

  @FOG_V1_ST06
  Scenario Outline: Durability failures cannot be acknowledged as success
    Given the storage service encounters <failure>
    When a mutation is attempted
    Then no uncommitted value is acknowledged as durable
    And affected writes fail closed without creating a fresh empty database

    Examples:
      | failure                         |
      | disk full                       |
      | a failed flush                  |
      | a corrupt database              |
      | missing persistent store identity |
      | effective synchronous mode NORMAL |
      | unavailable exclusive ownership |

  @FOG_V1_ST07
  Scenario: State adapter updates result fields only after verified completion
    Given an IGrainStorage caller with known State, ETag and RecordExists
    When a write conflicts or its result remains unresolved
    Then the adapter does not install a fabricated successful etag or state
    When a later read completes and its registered codec verifies the whole payload
    Then all three result fields correspond to that one snapshot

  @FOG_V1_ST08
  Scenario: Expired durable transaction IDs never allocate again
    Given a committed storage transaction whose retry record has expired
    When the service restarts and the old ID is submitted again
    Then it reports tx-expired or absence without rerunning the effect
    And the persistent allocation counter cannot reuse that ID

  @FOG_V1_ST09
  Scenario: Durable resource effects do not restore stale handles
    Given a resource create committed with its operation ledger before the resource host crashed
    When a freshly authorized caller resolves that operation identity
    Then the created object's stable identity is recoverable without another create
    And the preceding host's open handle is stale
    And fresh open authority is required to use the object

  @FOG_V1_ST10
  Scenario: Storage does not resurrect disposable fog state
    Given durable resource blobs beside ephemeral jobs, scopes and membership
    When the control process loses all ephemeral state and restarts
    Then resource blobs can be read under their storage contract
    And old jobs, AAN sessions, worker scopes and membership are not reconstructed from them

  @FOG_V1_ST11
  Scenario: Restoring an older database fences preceding etags
    Given the operator has drained and fenced the old storage writer
    When a consistent older backup is restored for service
    Then it receives a new store identity before accepting clients
    And old etags and transaction IDs cannot alias restored versions
    And this is not advertised as transparent failover
