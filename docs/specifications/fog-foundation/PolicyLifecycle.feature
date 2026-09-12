@fog_foundation @security
Feature: Operator policy changes are coherent and explicitly revoke authority
  A versioned public-key and permission bundle is not a store of user secrets.

  @FOG_P01
  Scenario Outline: Invalid configuration cannot replace active policy
    Given a committed policy bundle and a staged candidate with <defect>
    When the operator checks and applies that candidate
    Then validation fails and the committed generation remains active
    And no partial candidate permissions become visible

    Examples:
      | defect                                |
      | a mismatched companion-file digest    |
      | duplicate principal or key identities |
      | an identical duplicate row            |
      | an unknown schema column              |
      | an unresolved quota or namespace reference |
      | a public key assigned to two principals |
      | a symlink replacing a policy file     |
      | a namespace path containing traversal |
      | an unbounded or zero required quota   |

  @FOG_P02 @property
  Scenario: Policy activation cannot observe a changing validated bundle
    Given a checked candidate bundle and a concurrent file replacement
    When apply revalidates and commits the candidate
    Then either its exact checked bytes become active or activation fails
    And mixed-generation configuration is never acknowledged

  @FOG_P03
  Scenario Outline: A crash exposes one committed policy generation
    Given a transition from policy epoch 4 to epoch 5
    When the control host crashes <boundary>
    Then restart loads <epoch> and its exact bundle identity
    And missing committed files cause startup failure instead of policy rollback

    Examples:
      | boundary                   | epoch   |
      | before durable commit      | epoch 4 |
      | after durable commit       | epoch 5 |

  @FOG_P04
  Scenario: Policy epoch identities cannot be reused or rolled back
    Given policy epoch 5 is committed
    When the operator reapplies its identical bundle
    Then the operation returns the existing committed identity without a second transition
    When the operator supplies changed bytes under epoch 5 or an older epoch
    Then activation is rejected without replacing committed authority

  @FOG_P05 @cluster
  Scenario: Explicit revocation includes open handles and admitted jobs
    Given an authenticated root, open resource handle, queued job and running job under epoch 4
    When policy epoch 5 is committed
    Then new admissions pause and epoch 4 authentication and handles are invalidated
    And the queued job fails policy-changed without starting
    And the running job's authority is revoked and its worker is stopped before terminal failure
    And a late completion cannot publish a successful result
    And effects already committed are not reported as rolled back

  @FOG_P06 @cluster
  Scenario: A disconnected worker prevents premature revocation completion
    Given a partitioned worker with an unexpired epoch 4 execution lease
    When policy epoch 5 is committed
    Then status reports revocation pending and no new-generation jobs are admitted
    And the worker receives no renewal for epoch 4
    When the worker acknowledges stopping or its conservative lease and stop bound passes
    Then the revocation barrier can complete

  @FOG_P07
  Scenario: Key rotation does not silently retain an old login
    Given a principal whose old and new public keys are enrolled during a rotation window
    When a later policy removes the old key and its revocation barrier completes
    Then old-key proofs and old-generation sessions cannot access resources
    And fresh authentication using the enabled new key can access permitted retained results

  @FOG_P08 @fuzz
  Scenario: Remote privileges and malformed commands cannot administer the host
    Given a remote user and an OS-protected local administration socket
    When the user guesses admin paths or malformed and batched control commands are submitted locally
    Then remote administration is denied and malformed commands have no control side effect
    And errors and audit records contain no private key, proof or guest data
