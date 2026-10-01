@fog_foundation @cluster @wire
Feature: Foundation evidence exercises actual boundaries
  Mock success cannot prove authentication, containment or all-9P operation.

  @FOG_I01
  Scenario: An authenticated user submits bounded work on a second node
    Given a real control silo, a real worker silo and the configured 9P membership service
    And an independently running custom factotum with an enrolled test key
    And an installed real WASM provider forced onto the worker node
    When its client authenticates and submits a finite WASM job through the normal job files
    Then the worker executes the pinned program and returns the expected result
    And the user's private key remains local to factotum
    And the recorded remote service traffic is entirely 9P inside validated TLS

  @FOG_I02
  Scenario Outline: Actual provider containment survives non-completing work
    Given a registered real <runtime> provider and independently monitored worker resources
    When a job exceeds its declared work, memory or deadline allowance
    Then no successful result is published and actual execution stops within the declared bound
    And a subsequent unrelated job executes without private state from the failed job

    Examples:
      | runtime |
      | wasm    |

  @FOG_I03 @security
  Scenario: Factotum and the managed verifier agree on exact signed bytes
    Given shared test vectors for libtab-eddsa-blake2b-v1 and a canonical challenge
    When custom factotum produces a signed cell and the managed verifier checks it
    Then the decoded body is byte-for-byte equal to that challenge
    And changed bytes, the wrong public key and the Ed25519 algorithm are rejected
    And C and .NET LibTab readers agree on the enclosing proof document

  @FOG_I04 @security
  Scenario: A stock 9P client does not bypass the authentication profile
    Given a general 9P client without the fog proof exchange
    When it attaches to the protected user export with NOFID and an enrolled username
    Then it is denied rather than treated as authenticated by protocol compatibility
    And a client implementing the documented afid payload can use the same standard 9P messages

  @FOG_I05
  Scenario: Cross-node policy revocation is observed during a real partition
    Given a running job on a separately isolated worker and an authenticated result reader
    When that worker is partitioned and the control node applies a new policy epoch
    Then the old reader loses authority and global revocation remains pending
    And the actual worker stops after its unrenewed lease within the declared containment bound
    And reconnecting cannot publish its old-generation completion
