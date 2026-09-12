@libtab_jobs
Feature: Different execution engines obey one bounded job lifecycle
  Runtime-specific resource allowances complement shared memory, output, and deadline
  limits. No engine can bypass the job's namespace or admission policy.

  @JOB_R01 @property
  Scenario Outline: The selected engine requires its own declared work limits
    Given a valid common job description for <runtime>
    When its <required> field is missing and the client starts the job
    Then validation fails instead of borrowing another runtime's budget units

    Examples:
      | runtime | required       |
      | math    | p_cpu_ms       |
      | wasm    | fuel           |

  @JOB_R02 @security
  Scenario Outline: Unsupported execution choices are not silently substituted
    Given a sealed job description requesting <choice>
    When the client starts the job
    Then validation or admission rejects the incompatible request
    And no different engine, artifact, or unrestricted fallback runs in its place

    Examples:
      | choice                                      |
      | an unknown runtime name                     |
      | AngouriMath with a WASM fuel field instead of p_cpu_ms |
      | a WASM module with unsupported required features |

  @JOB_R03 @property
  Scenario Outline: Runtime budget exhaustion terminates unfinished computation
    Given a <runtime> job containing computation that cannot finish within its <budget>
    When the selected runtime executes that job
    Then the job fails with <reason> at its declared enforcement boundary
    And no further guest work executes after termination
    And its private execution resources are reclaimed

    Examples:
      | runtime | budget | reason     |
      | math    | p_cpu_ms | cpu-limit |
      | wasm    | fuel   | fuel-limit |

  @JOB_R04
  Scenario: Expensive primitives and host calls cannot evade execution limits
    Given an AngouriMath operation or WASM host call held at a controlled long-running barrier
    And its job has a finite admitted deadline and host-resource allowance
    When that deadline or allowance is exhausted
    Then the job terminates without waiting indefinitely for the primitive or host call
    And it does not continue consuming unaccounted host resources

  @JOB_R05 @property
  Scenario Outline: Shared job budgets apply to every runtime
    Given a running <runtime> fixture workload
    When it attempts to exceed its configured <limit>
    Then the job fails with the corresponding limit reason
    And the receiving client cannot read partial output as a successful result
    And job-private resources return to the idle baseline

    Examples:
      | runtime | limit        |
      | math    | memory_bytes |
      | wasm    | memory_bytes |
      | math    | output_bytes |
      | wasm    | output_bytes |
      | math    | deadline_ms  |
      | wasm    | deadline_ms  |

  @JOB_R08 @cluster
  Scenario: Placement selects a worker that can satisfy the frozen job
    Given workers with different runtimes, artifact versions, hardware features, and capacity
    When a job is admitted for execution
    Then it runs only on a worker matching its pinned runtime and artifact requirements
    And capacity is reserved within node and principal limits
    And artifact transfer and remote execution coordination use the authorized 9P paths

  @JOB_R09 @security
  Scenario Outline: Uploaded code receives only explicit capabilities
    Given a sandboxed job without a capability for <resource>
    When its program attempts to access that resource
    Then access is denied without escaping the worker's execution boundary
    And no unauthorized external effect occurs

    Examples:
      | resource                  |
      | the host filesystem       |
      | arbitrary CLR reflection  |
      | creating a host process   |
      | opening a raw network socket |
      | another principal's job   |
      | the Orleans transport channel |

  @JOB_R10 @security
  Scenario: Artifact paths cannot bypass namespace authorization
    Given a job referencing source or input data outside its authorized namespace
    When validation resolves the reference
    Then the job is rejected before reading unauthorized bytes or invoking a runtime
    And a path string, digest, or cached copy does not grant missing authority

  @JOB_R11
  Scenario: Loading work is bounded before guest metering begins
    Given a worker preparing a large module or source file under finite preparation limits
    When decoding, compilation, or loading exceeds a preparation limit
    Then preparation terminates with a bounded failure
    And no guest execution starts
    And partially loaded job-private resources are reclaimed
