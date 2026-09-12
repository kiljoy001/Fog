@workload_providers
Feature: A provider executes through a bounded per-job context
  The provider owns its algorithm and engine resources. The host owns authority,
  limits, state publication, and containment of failures or non-cooperative work.

  @PROV_E01 @property
  Scenario: Reusing a provider never reuses mutable execution state
    Given a provider enabled for two jobs with different inputs and credentials
    When their preparation and execution are interleaved
    Then PrepareAsync returns a different execution object for each job
    And each context contains only its own frozen specification and authorized streams
    And neither job can observe the other's private mutable state

  @PROV_E02
  Scenario: Provider preparation receives the admitted immutable job
    Given a job admitted with a validated provider descriptor and pinned source artifact
    When the assigned worker calls PrepareAsync
    Then the context exposes the admitted specification values and artifact bytes
    And changing the original upload, path, or registration object cannot change them
    And preparation cannot publish result bytes or perform external namespace writes

  @PROV_E03 @property
  Scenario: Work charges enforce an exact host-owned limit
    Given a provider with a nodes meter limited by p_max_nodes
    And the fixture records each node operation independently of meter calls
    When the provider charges one unit before each generated node operation
    Then work up to the declared limit is allowed
    And the charge for the first excess operation fails before that operation executes
    And p_nodes_used reports the host-recorded consumption without overflow

  @PROV_E04 @security
  Scenario: Catching a limit exception cannot publish false success
    Given an execution whose work meter has exhausted its admitted limit
    When the provider catches the charge failure and returns a successful completion
    Then the host preserves the latched limit failure
    And it does not publish the staged output as a successful result
    And the provider cannot reset or increase the exhausted budget

  @PROV_E05 @property
  Scenario Outline: Provider failures remain job-local and clean up ownership
    Given an execution fixture that fails during <phase>
    When the host advances that job through the failing phase
    Then the job reaches a bounded sanitized failure
    And its private resources are reclaimed or its isolated worker is terminated
    And another job can continue on an unaffected compatible worker

    Examples:
      | phase   |
      | prepare |
      | run     |
      | stop    |
      | dispose |

  @PROV_E06
  Scenario: Stop can run while execution is blocked
    Given an execution whose RunAsync is waiting behind a controlled barrier
    When its job is cancelled
    Then StopAsync is invoked without waiting for RunAsync to finish
    And the host observes execution termination before publishing cancelled
    And DisposeAsync is invoked once for the returned execution

  @PROV_E07 @security
  Scenario: Non-cooperative preparation cannot occupy a silo indefinitely
    Given a provider whose PrepareAsync ignores cancellation and never returns
    And its host-enforced isolated worker has a finite preparation deadline
    When that deadline expires
    Then the host terminates the affected worker within the cleanup policy
    And job control and unrelated silo work remain responsive

  @PROV_E08 @property
  Scenario: Late preparation completion is disposed without executing
    Given a job cancelled while PrepareAsync is pending
    When preparation returns an execution after cancellation has won
    Then RunAsync is not invoked on that execution
    And the returned execution is stopped and disposed
    And no late callback changes the terminal job outcome

  @PROV_E09 @security
  Scenario: The execution context cannot outlive the job
    Given an execution that retained an input, output, or namespace handle
    When that job terminates and context ownership is revoked
    Then later use of the retained handle is rejected
    And it cannot append output or modify another job after termination

  @PROV_E10
  Scenario: Returning completion does not bypass output publication rules
    Given a provider that has written partial bytes to its output stream
    When a client attempts to read result before the host accepts completion and cleanup
    Then the service reports result-not-ready
    When the execution and cleanup succeed without a latched host failure
    Then the completed bytes and succeeded status are published coherently
