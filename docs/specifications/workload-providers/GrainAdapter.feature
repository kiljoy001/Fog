@workload_providers
Feature: The shared grain adapter owns the file protocol for every provider
  A workload author implements execution rather than duplicating the job
  filesystem, request tracking, admission, and cleanup state machines.

  @PROV_G01 @cluster @wire
  Scenario: A new provider runs without changing the grain adapter
    Given the shared adapter and a separately built third-party fixture provider
    And the provider has been enabled by explicit host registration
    When a stock 9P client submits a job using that provider's LibTab options
    Then the unchanged adapter performs allocation, sealing, validation, and admission
    And the provider runs the workload and returns the expected bytes
    And the client reads status and result through the standard job files
    And no new grain RPC interface or private 9P operation is required from the client

  @PROV_G02 @cluster
  Scenario: A running workload does not serialize away its own cancellation
    Given a workload held in a long-running execution outside the grain's control turn
    When a client requests status and then cancellation through the job files
    Then the adapter serves control requests while execution remains pending
    And cancellation reaches the execution boundary without waiting for natural completion

  @PROV_G03 @wire
  Scenario: Worker-local capabilities are not serialized into remote grain calls
    Given a job routed to a worker on another silo
    When the worker constructs its execution context
    Then streams and execution objects are created locally from authorized job resources
    And the network carries 9P resource data and serialized identities rather than those local objects
    And no raw service container or unrestricted grain factory is passed to provider code

  @PROV_G04 @property
  Scenario: Provider output cannot replace host-owned lifecycle metadata
    Given a provider attempting to report another job ID, state, or limit through its result bytes
    When that result is published
    Then those bytes remain payload rather than instructions to the adapter
    And the host-owned job identity, accounting, and terminal state remain authoritative

  @PROV_G05 @cluster @wire
  Scenario: Built-in and third-party providers use the same conformance workflow
    Given registered AngouriMath, WASM, and third-party fixture providers
    When the common job lifecycle and IO conformance suite is applied to each provider
    Then each obeys the same admission, cancellation, result, isolation, and cleanup rules
    And their runtime-specific budgets remain explicit
    And all inter-node provider and adapter communication remains 9P

  @PROV_G06
  Scenario: Non-job resource grains keep the existing resource extension point
    Given an existing custom IMountableResourceGrain exposing a resource tree
    When it is registered beside the shared workload adapter
    Then clients can continue to walk, open, read, write, stat, and clunk its supported resources
    And that grain is not required to pretend its persistent resource tree is a stateless compute job
