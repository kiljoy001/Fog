@workload_providers
Feature: Workload providers extend the job service without changing its protocol
  Runtime names are explicit versioned registrations, not a hard-coded list or a
  client-controlled mechanism for loading arbitrary host code.

  @PROV_P01 @wire
  Scenario: A separately authored provider is discoverable as LibTab metadata
    Given an administrator has enabled a separately built pathfinding provider
    When a 9P client reads its versioned manifest and options files
    Then both files are complete read-only LibTab snapshots
    And they identify the registered runtime, version, API, options, and isolation requirement
    And discovery alone does not reserve execution capacity

  @PROV_P02
  Scenario: Duplicate provider identities fail registration
    Given an enabled provider identified by runtime pathfinding and version 1.0.0
    When another registration claims the same identity
    Then registration fails without replacing the first provider
    And no job is dispatched through an ambiguous provider mapping

  @PROV_P03 @property
  Scenario Outline: Invalid provider descriptors fail before serving jobs
    Given a proposed registration with <defect>
    When the host validates the registration
    Then the provider is not enabled
    And a bounded diagnostic identifies the incompatible declaration

    Examples:
      | defect                                      |
      | an unsupported provider API                  |
      | a malformed LibTab manifest                  |
      | an empty runtime name                       |
      | a version containing a path separator        |
      | a traversal component in its identity        |
      | a provider option shadowing memory_bytes     |
      | a provider option without the p_ prefix      |
      | duplicate option names                      |
      | an unbounded text option                     |
      | a uint option with maximum below minimum     |
      | a default outside its declared bounds        |
      | duplicate meter names or usage fields        |
      | a work meter declared on a boolean option    |

  @PROV_P04 @property
  Scenario Outline: Job options are checked against the selected provider version
    Given an enabled provider with frozen option declarations
    When a client submits a job with <defect>
    Then validation rejects the job before PrepareAsync is called
    And no option is silently renamed, truncated, defaulted to unlimited, or ignored

    Examples:
      | defect                                     |
      | an undeclared p_ field                      |
      | a missing required provider option         |
      | semantic nil for a required option          |
      | a literal nil value for a uint option       |
      | a boolean value other than true or false    |
      | a value exceeding the declared maximum      |
      | a value exceeding the principal quota       |
      | an option declared only in another version  |

  @PROV_P05
  Scenario: Explicit optional defaults become part of the frozen job
    Given a provider with optional p_diagonal defaulting explicitly to false
    When a client omits that option and the job is admitted
    Then the provider receives p_diagonal as false in its immutable specification
    And replacing registration metadata later cannot change that admitted value

  @PROV_P06 @cluster
  Scenario: Placement preserves exact provider implementation identity
    Given an admitted job pinned to a provider version and implementation identity
    And another worker advertises the same name and version with different implementation bytes
    When the scheduler places or replaces the executing worker
    Then that incompatible implementation is not selected
    And no implicit latest-version or alternate-provider fallback occurs

  @PROV_P07 @security
  Scenario: A job cannot load an arbitrary provider
    Given a client with permission to submit jobs but not install providers
    When its job names an unregistered runtime, CLR type, assembly, or executable path
    Then admission rejects the unknown provider identity
    And the host does not reflectively instantiate, download, or launch the requested code

  @PROV_P08 @security
  Scenario: Declared isolation must be provided by the host
    Given a provider requiring process isolation
    And a worker that cannot enforce the required process resource and termination controls
    When that worker is considered for a job
    Then it is rejected as incompatible
    And a provider's self-reported isolation claim does not bypass that decision
