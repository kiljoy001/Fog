@fog_foundation
Feature: Basic placement remains bounded across contention and failure
  Disposable execution does not mean unaccounted capacity or automatic replay.

  @FOG_R01 @property
  Scenario: Concurrent admission cannot oversubscribe a principal
    Given a principal with capacity for one additional queued job and output reservation
    When two valid starts race at the admission barrier
    Then at most one new job is admitted within the remaining allowance
    And the other remains unstarted with a bounded admission error
    And retries and cancellation cannot release or charge either reservation twice

  @FOG_R02
  Scenario: An unplaceable principal cannot block other runnable work
    Given one principal whose oldest job cannot fit available compatible capacity
    And another principal with an eligible queued job
    When the scheduler advances its round-robin selection
    Then the eligible job can run without reordering jobs within its own principal queue
    And the unplaceable job remains bounded by its original deadline and reservation

  @FOG_R03
  Scenario: Placement preserves compatibility before applying preferences
    Given a local worker with incompatible code and two compatible remote workers
    When the control node selects a worker for an admitted job
    Then it ignores the incompatible local worker
    And it applies cached-artifact, reserved-memory-fraction and node-ID preferences in order
    And it reserves running capacity before preparation starts

  @FOG_R04 @fuzz
  Scenario: Artifact copies become reusable only after bounded verification
    Given a missing artifact with a pinned digest and finite transfer allowance
    When its copy is interrupted, oversized or digest-corrupted
    Then no partial copy is published or advertised as cached
    And no runtime executes from those bytes
    And staging storage and failed reservations are reclaimed

  @FOG_R05
  Scenario: A cache cannot evict active bytes or grant access by possession
    Given a verified cache entry pinned by a running job and a full cache budget
    When another job requests space or an unauthorized principal requests that artifact
    Then the active entry is not evicted and allocation stays within the cache budget
    And the unauthorized request cannot read the cached bytes

  @FOG_R06 @property @cluster
  Scenario: Delayed renewal replies do not extend an expired execution lease
    Given a worker that sent a renewal request under its current boot and policy epoch
    When its local monotonic request deadline expires before the reply arrives
    Then the late reply cannot renew or resurrect the job scope
    And host calls are denied and containment stops the expired execution
    And duplicate or preceding-boot replies cannot reset the deadline

  @FOG_R07 @cluster
  Scenario: Control loss does not leave native execution running indefinitely
    Given a native job held in a non-cooperative execution barrier
    When the control node becomes unreachable and its worker lease expires
    Then an independent host watchdog stops that execution within the containment bound
    And the worker cannot publish new successful output under the expired scope
    And no new jobs are admitted through a replacement authority

  @FOG_R08 @cluster
  Scenario: Control restart fences the preceding execution generation
    Given an admitted job that may have produced external effects before control loss
    When the control node restarts from its protected local state directory
    Then it uses a fresh boot ID and does not admit work before the prior-lease safety barrier
    And old job IDs report job-unavailable rather than a fabricated outcome
    And old worker completions cannot publish into the new boot's jobs
    And the old job is never automatically rerun

  @FOG_R09
  Scenario: Worker restart reconciles old child processes before announcing capacity
    Given a worker process that crashed while its isolated execution child remained alive
    When that worker restarts
    Then it kills and reaps the old owned child before announcing a new incarnation
    And failure to reconcile ownership prevents the worker from becoming ready

  @FOG_R10
  Scenario: Session expiry does not cancel already admitted work
    Given an admitted job and a submitting session whose maximum lifetime expires
    When the host invalidates that session's fids
    Then its admitted job retains its original deadline and execution scope
    When its owner authenticates again under unchanged policy before result expiry
    Then the owner can inspect the same retained job without another execution

  @FOG_R11
  Scenario: Drain reports completion only after execution and accounting are reconciled
    Given queued and running jobs and a finite drain deadline
    When the operator drains the control node
    Then new clone allocations and admissions are rejected
    And accepted jobs may finish only within their original limits
    When the drain deadline expires
    Then remaining jobs are stopped through containment
    And drained is reported only after no live execution or unreconciled reservation remains

  @FOG_R12 @property
  Scenario: Health reads are coherent and audit loss is detectable
    Given changing host counts and a full bounded audit ring
    When a client reads a status document in fragments and an operator reads audit records
    Then status fragments belong to one immutable per-open snapshot
    And audit sequence gaps expose overwritten records without blocking control progress
    And neither output includes proofs, private keys or guest payloads

  @FOG_R13 @cluster
  Scenario: Shorter new lease settings cannot shorten the old restart barrier
    Given an old boot issued leases under a durably recorded conservative bound
    When the control node restarts with a shorter configured lease interval
    Then admission still waits the previously persisted safety bound
    And a corrupt or missing prior bound blocks automatic recovery
