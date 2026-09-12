@libtab_jobs
Feature: Jobs release execution state without losing their bounded observable outcome
  Client connections, retained results, and execution contexts have different
  lifetimes. Cancellation and retries never imply rollback of external effects.

  @JOB_L01
  Scenario Outline: Terminal jobs release execution resources before client release
    Given an admitted job with private runtime state and an open input handle
    When the job reaches <outcome>
    Then its mutable runtime context, private buffers, and input handles are reclaimed
    And only bounded status and permitted completed result bytes remain retained
    And cleanup does not wait for the client to send release

    Examples:
      | outcome   |
      | succeeded |
      | failed    |
      | cancelled |

  @JOB_L02 @security
  Scenario: Warm caches never retain another job's mutable execution state
    Given a completed job that used private input, credentials, and mutable runtime state
    And its verified immutable module remains in a bounded shared cache
    When another authorized job reuses the cached artifact
    Then it receives a fresh mutable execution context
    And it cannot observe the preceding heap, input, or credentials
    And cached bytes remain subject to the node cache budget

  @JOB_L03
  Scenario: Cancelling queued work prevents execution
    Given an admitted queued job not yet assigned to an executing worker
    When its owner writes cancel to ctl
    Then the job is removed from the runnable queue and reaches cancelled
    And its runtime never starts
    And repeating cancel preserves that outcome

  @JOB_L04
  Scenario: Cancellation of native work is not merely cancellation of the wait
    Given a running native runtime worker that does not cooperate with cancellation
    When the owner requests cancellation
    Then status reports cancellation requested while execution is being stopped
    And an enforceable worker boundary stops it within the cleanup deadline
    And cancelled is published only after its private execution resources are reclaimed
    And no late output is published as a successful result

  @JOB_L05 @property
  Scenario: Completion and cancellation publish one terminal outcome
    Given completion and cancellation held at competing controlled barriers
    When those barriers are released in generated orders
    Then exactly one terminal outcome is published
    And status and result agree with that winner
    And a late callback cannot overwrite the terminal state or resurrect execution

  @JOB_L06
  Scenario: Queue time consumes the admitted deadline
    Given an admitted job held in a bounded queue
    When its controlled deadline expires before execution can begin
    Then it fails with deadline without starting its runtime
    And its reserved queue capacity is released

  @JOB_L07 @wire
  Scenario: Terminal session loss and flushing a start reply are not job cancellation
    Given a job whose start has been accepted
    When the client flushes its pending start request and closes its logical session
    Then transport reply ordering obeys the 9P flush barrier
    And the accepted job is not automatically cancelled or rolled back
    When the owner reconnects before retention expires
    Then the same job can be inspected and explicitly cancelled if still active

  @JOB_L08
  Scenario: Release refuses active work but removes completed ownership
    Given a running job
    When the owner writes release to ctl
    Then release is rejected with job-active and the running job is unchanged
    When the job terminates and the owner releases it
    Then its retained files and output are removed and existing job fids become invalid
    And a late completion cannot recreate the removed job

  @JOB_L09
  Scenario Outline: Abandoned metadata has a bounded lifetime
    Given an unreleased job in <state> with a configured expiry
    When controlled time passes that expiry
    Then the job's retained metadata, files, and staging buffers are reclaimed
    And later access cannot allocate a replacement under the stale job ID

    Examples:
      | state     |
      | staging   |
      | succeeded |
      | failed    |
      | cancelled |

  @JOB_L10 @cluster
  Scenario: A lost worker does not cause silent replay of external effects
    Given a job that may have changed an external resource
    And its coordinator remains available
    When the executing worker is lost before reporting a definitive outcome
    Then the job reports a failed worker-lost outcome with effects potentially unknown
    And the service does not silently execute that job again
    And an explicit application retry requires provider-owned idempotency for those effects
