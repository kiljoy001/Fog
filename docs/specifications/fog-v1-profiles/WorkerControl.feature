@fog_v1_profiles
Feature: Worker coordination preserves one bounded owner of admitted work
  A cached scope or successful transport replay cannot extend execution authority.

  @FOG_V1_WC01 @security
  Scenario: Advertisements cannot create authority or capacity
    Given an enrolled worker with configured slot and memory limits
    When it advertises excess capacity or an unregistered provider bundle
    Then control rejects the incompatible claim rather than raising its configured caps
    And advertisement alone neither admits a job nor grants a scope

  @FOG_V1_WC02 @property
  Scenario: Lost poll replies do not duplicate assignments
    Given an admitted job and a compatible worker with reserved offer capacity
    When its first poll reply is lost and it polls again
    Then it receives the existing unacknowledged offer for that job
    And that job has exactly one worker assignment and one reservation

  @FOG_V1_WC03
  Scenario: Expiring an unacquired offer cannot authorize later execution
    Given an offered job whose preparation allowance expires before acquire
    When its worker later submits acquire for that offer
    Then the stale offer is rejected and no guest preparation or execution starts
    And the original offer's reserved capacity is released once

  @FOG_V1_WC04 @property
  Scenario: Transport queue time consumes a lease grant
    Given a worker captured its lease request start before enqueueing to AAN
    When network delay consumes the entire granted interval before the reply arrives
    Then the worker rejects the grant without starting or reviving execution
    And receipt time is not substituted for the original request time

  @FOG_V1_WC05 @property
  Scenario: Run permission is not a second execution request
    Given a supervisor has prepared one execution under a valid lease
    When prepared replies are lost or duplicated and the owner recovers its run permission
    Then the supervisor invokes RunAsync exactly once for that execution key
    And duplicate permission cannot allocate another process or extend the lease

  @FOG_V1_WC06 @property
  Scenario Outline: Invalid renewals never extend execution
    Given a leased worker scope
    When a renewal has <defect>
    Then it cannot extend the current usable interval
    And any expiry still triggers independent containment

    Examples:
      | defect                              |
      | an old sequence                     |
      | a skipped sequence                  |
      | a changed worker boot               |
      | a changed policy epoch              |
      | a response arriving after expiry    |
      | a changed payload for a duplicate transaction |

  @FOG_V1_WC07 @security
  Scenario Outline: Scope attach is bound to the assigned worker
    Given a resource host with a frozen scope and current resource lease
    When a caller attempts scope attach with <identity>
    Then it receives no resource authority or retained handle

    Examples:
      | identity                        |
      | a public user factotum proof    |
      | another enrolled worker's TLS identity |
      | a preceding worker incarnation  |
      | only a known scope ID           |

  @FOG_V1_WC08 @property
  Scenario: Scope leases bound policy revocation during a partition
    Given a resource host has a scope lease timed from its original request
    When control changes policy and the resource host is partitioned
    Then the host denies new operations by its existing lease expiry
    And delayed lease responses cannot restart authority
    And global revocation remains pending until the conservative resource-lease barrier

  @FOG_V1_WC09 @security
  Scenario: Frozen scope files cannot widen across transport or placement
    Given an admitted scope with pinned namespace and artifact digests
    When a worker receives changed mounts, grants, artifact bytes or provider identity
    Then preparation rejects the mismatch before guest execution
    And a cached artifact digest does not grant access outside that scope

  @FOG_V1_WC10 @property
  Scenario: Completion publishes one verified outcome and releases capacity once
    Given a current worker has reaped execution and staged a complete bounded result
    When finish and cancellation race at the control publication barrier
    Then exactly one terminal outcome is retained
    And successful output exists only if success won with a valid scope and digest
    And duplicate finish or stopped reports cannot release reservations twice

  @FOG_V1_WC11
  Scenario: Expired assignments may report stopped but cannot publish success
    Given a worker's execution scope has expired and its containment has been reconciled
    When the matching old worker reports stopped and then attempts a successful finish
    Then stopped may reconcile ownership without extending authority
    And the late success cannot replace the host's terminal decision

  @FOG_V1_WC12 @cluster
  Scenario: Worker loss does not silently reassign uncertain work
    Given an acquired assignment may have issued external effects
    When its worker disappears while a different compatible worker is available
    Then control waits the conservative stop barrier before releasing uncertain capacity
    And it does not automatically execute that same job on the other worker
    And the failed outcome preserves uncertainty about already accepted effects

  @FOG_V1_WC13 @security @property
  Scenario Outline: Destination checks cannot be bypassed by a context-free resource method
    Given a job-scoped resource invocation or handle at a destination silo
    When its invocation has <defect>
    Then the destination filter rejects it before the provider performs the operation
    And no reserved authority context leaks into the next unrelated invocation

    Examples:
      | defect                                      |
      | a missing job envelope on ReadAsync         |
      | a changed scope owner in a User field       |
      | an expired destination resource lease      |
      | an issuer from a preceding node boot        |
