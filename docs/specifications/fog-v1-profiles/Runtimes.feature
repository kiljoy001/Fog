@fog_v1_profiles
Feature: Initial runtime profiles have explicit semantics and bounded execution
  Engine defaults and a successful process exit cannot override the host contract.

  @FOG_V1_RT06 @wire
  Scenario: A WASI command has exact bounded standard streams
    Given a conforming wasm32 WASIp1 module which copies stdin to stdout
    When it runs with a generated binary input and sufficient limits
    Then result bytes exactly equal that input without a prefix or added LF
    And argv contains only fog-wasm and environment is empty
    And stderr is separate bounded diagnostic data

  @FOG_V1_RT07 @security @fuzz
  Scenario Outline: Unsupported WASM choices do not enable a stronger ABI
    Given a submitted module requiring <feature>
    When the profile validates or links it
    Then it fails before executing guest code
    And no implicit runtime or feature fallback occurs

    Examples:
      | feature                         |
      | a component rather than a core module |
      | shared memory and threads       |
      | memory64                        |
      | a custom raw socket import      |
      | an imported linear memory       |
      | a core start section            |
      | a native precompiled cache file |
      | a missing or wrongly typed _start |

  @FOG_V1_RT08
  Scenario: Core start sections cannot call imports before memory binding
    Given a module with a core start section
    When preparation validates the namespace profile
    Then it rejects the module without executing guest instructions
    And instantiation and _start are not invoked
    And no partial output is published

  @FOG_V1_RT09 @property
  Scenario: Host calls cannot bypass host IO budgets and pointer bounds
    Given a WASI call with generated buffer lengths and linear-memory pointers
    When the runner validates and attempts the call
    Then it rejects invalid memory ranges before copying
    And valid calls charge their full requested capacity before host work
    And wrapping vector totals cannot create free or out-of-bounds IO
    And invalid calls still consume a host-call unit

  @FOG_V1_RT10 @security
  Scenario: WASI has no inherited host directories or network capabilities
    Given a WASI command attempting host path access and socket operations
    When it executes under fog-wasi-namespace-v1
    Then it receives the defined WASI errors without accessing those resources
    And repeated calls still consume bounded host-call allowance and deadline
    And explicit virtual preopens grant no inherited Linux directory access

  @FOG_V1_RT11
  Scenario Outline: WASM process outcomes cannot conceal execution failure
    Given a WASI command ending with <event>
    When the host considers publication
    Then it reports <outcome>
    And successful output is unavailable for failed outcomes

    Examples:
      | event                      | outcome                 |
      | proc_exit with zero        | candidate success       |
      | proc_exit with nonzero     | wasm-exit failure       |
      | a runtime trap             | wasm-trap failure       |
      | a latched memory violation followed by exit zero | memory-limit failure |
      | stderr beyond its allowance | diagnostic-limit failure |

  @FOG_V1_RT16 @cluster
  Scenario Outline: Real engines run behind the same provider and job interface
    Given the registered <runtime> bundle and an actual isolated execution host
    When a stock 9P client submits its valid bounded fixture job
    Then the common job files expose the profile's expected result and usage
    And all service traffic remains 9P with no engine-specific network server
    And the process is reaped before successful terminal publication

    Examples:
      | runtime |
      | wasm    |
