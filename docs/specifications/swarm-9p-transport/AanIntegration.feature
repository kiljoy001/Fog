@swarm_transport @aan
Feature: Fog work remains bounded while its 9P transport is suspended
  Transport continuity does not stop clocks or replace application failure handling.

  @SW9P_AI01
  Scenario: AAN bootstrap and cleanup do not depend on Orleans
    Given the built-in host and client transports are configured for the chosen AAN profile
    And no external aan executable, grain activation or remote grain call is available
    When authorized endpoints establish, suspend, resume and close a logical session
    Then all four transitions complete using host-local transport services
    And closure returns owned resources to the idle baseline

  @SW9P_AI02 @wire
  Scenario: A temporary break preserves an unsealed job upload
    Given an authorized resumable session with a staging upload writer
    And neither the staging lease nor the session deadline has expired
    When the carrier breaks during upload and the same session resumes
    Then the writer and its exact accepted prefix are preserved
    And the client continues through the same fid at the next logical upload offset
    When it completes and clunks that upload
    Then one exact complete revision is sealed without duplicated bytes or a new writer
    And resumption itself has not admitted a job

  @SW9P_AI03
  Scenario: Terminal session loss discards an unsealed replacement
    Given an AAN session replacing a previously sealed staging input
    When the carrier fails and the session's resume deadline expires
    Then the private incomplete replacement is discarded
    And the preceding sealed revision remains unchanged while the job remains retained
    And no clunk, seal or start is synthesized during cleanup

  @SW9P_AI04 @property
  Scenario Outline: Suspension cannot pause workload or retention limits
    Given a job governed by a finite <limit> and a suspended submitting session
    When that limit is reached independently of carrier recovery
    Then its normal enforcement and cleanup take place
    And later resumption cannot reset that limit or resurrect removed state

    Examples:
      | limit                   |
      | Worker CPU allowance     |
      | WASM fuel allowance     |
      | job deadline            |
      | staging expiry          |
      | result retention expiry |

  @SW9P_AI05 @cluster
  Scenario: Resumption cannot turn stale buffered traffic into a lease renewal
    Given a real control node and worker connected through AAN
    And the worker has a running job and an outstanding timed lease renewal
    When a partition lasts beyond the worker's lease
    Then the independent watchdog stops execution within its containment bound
    And AAN probes do not renew the lease or report membership heartbeats
    When the carrier resumes and delivers the delayed renewal reply
    Then the expired scope remains invalid and the job is not restarted
    And no stale result or host call is accepted as current authority

  @SW9P_AI06 @cluster @wire
  Scenario: A temporary silo link break does not duplicate a grain dispatch
    Given two real silos and an external client using the chosen AAN and 9P profile
    And an invocation is dispatched once to the target silo without application retries
    When the carrier carrying its reply breaks and resumes before RPC and membership deadlines
    Then the external client receives the original result
    And the target invocation count remains one
    And an independent recorder decodes all layers of the chosen transport profile
    And Orleans payloads remain inside logical 9P file data with no native RPC connection

  @SW9P_AI07 @cluster
  Scenario: Host loss remains an uncertain application failure
    Given a remote mutation may have executed before its result reached the caller
    When its host loses all logical session and replay state
    Then AAN cannot resume that session on the restarted host or another silo
    And the caller observes an uncertain outcome within its configured deadline
    And any new session uses fresh negotiation and authentication
    And retrying the effect requires application identity and provider-owned idempotency

  @SW9P_AI08 @wire
  Scenario: Direct clients retain the existing non-resumable contract
    Given a separately configured direct 9P endpoint and an authorized stock client
    When the client opens a resource and its physical connection terminates
    Then the direct session's fids and pending operations are cleaned up
    And reconnecting requires a fresh 9P session without an AAN exchange
    And the direct endpoint is not an automatic fallback from an AAN-required endpoint
