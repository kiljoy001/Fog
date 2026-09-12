@fog_v1_profiles
Feature: Initial runtime profiles have explicit semantics and bounded execution
  Engine defaults and a successful process exit cannot override the host contract.

  @FOG_V1_RT01 @property
  Scenario Outline: AngouriMath runs exact scalar and symbolic golden vectors
    Given a fresh fog-math-v1 job using the actual pinned AngouriMath bundle
    When its operation is <operation> with source <source> and variable <variable>
    Then its mathematical result is <result>
    And the complete fogmath-result-v1 bytes match the bundle's committed golden vector
    And status reports completed only after execution cleanup

    Examples:
      | operation     | source          | variable | result              |
      | evaluate      | 1/2 + 1/3       | absent   | 5/6                 |
      | evaluate      | 1 + 2*2 + 3*2^2 | absent   | 17                  |
      | simplify      | x + x           | absent   | 2*x                 |
      | differentiate | 1 + 2*x + 3*x^2 | x        | 2 + 6*x             |
      | solve         | x^2 - 4         | x        | real roots -2 and 2 |
      | solve         | x^2 + 1         | x        | an empty root set   |

  @FOG_V1_RT02 @property
  Scenario Outline: CPU allowance includes preparation and serialization
    Given a controlled per-job cgroup CPU counter and an allowance of 100 milliseconds
    When <event>
    Then the host reports <outcome>
    And no failure exposes partial successful output

    Examples:
      | event                                                | outcome              |
      | final cumulative usage is 99 milliseconds             | candidate success    |
      | a sample reaches 100 milliseconds during parsing      | cpu-limit failure    |
      | final cumulative usage reaches 100 during printing    | cpu-limit failure    |
      | a sample overshoots 100 while algebra ignores cancellation | cpu-limit failure |

  @FOG_V1_RT03 @fuzz
  Scenario Outline: Math rejects unsupported or structurally excessive expressions
    Given a fog-math-v1 source with <defect>
    When its tokens and engine AST are validated within the isolated worker
    Then it fails within its structural, preparation and execution bounds
    And no host call or unrelated job state is exposed

    Examples:
      | defect                              |
      | assignments or a second expression  |
      | a nesting depth above 256           |
      | a numeric value above the bit bound |
      | a zero rational denominator         |
      | a function or CLR method invocation |
      | a negative or symbolic exponent     |
      | a solve polynomial of degree five   |
      | invalid UTF-8 or a raw NUL           |

  @FOG_V1_RT04
  Scenario: Expressions cannot create state for another job
    Given a math job containing an attempted binding or host operation
    When the provider validates its source
    Then it rejects the unsupported expression before algebra execution
    When another job starts in its fresh process
    Then no expressions, bindings or settings from the earlier job are inherited

  @FOG_V1_RT05 @security
  Scenario: Cooperative cancellation is backed by process containment
    Given a math engine call held at a barrier that ignores its cancellation token
    When its CPU allowance or admission-based deadline expires
    Then the supervisor kills and reaps the whole job cgroup
    And no output becomes a successful result
    And CPU sampling overshoot is recorded rather than reported as exact instruction fuel

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
      | a native precompiled cache file |
      | a missing or wrongly typed _start |

  @FOG_V1_RT08
  Scenario: WASM start sections consume run fuel rather than preparation time only
    Given a valid module with a looping start section and a finite fuel allowance
    When preparation completes and run begins
    Then preparation has executed no guest instructions
    And instantiation fails with fuel-limit before invoking _start
    And no partial output is published

  @FOG_V1_RT09 @property
  Scenario: Host calls cannot bypass WASM fuel and pointer bounds
    Given a WASI call with generated buffer lengths and linear-memory pointers
    When the runner validates and attempts the call
    Then it rejects invalid memory ranges before copying
    And valid calls charge their full requested capacity before host work
    And wrapping vector totals cannot create free or out-of-bounds IO

  @FOG_V1_RT10 @security
  Scenario: WASI has no inherited host directories or network capabilities
    Given a WASI command attempting host path access and socket operations
    When it executes under fog-wasi-v1
    Then it receives the defined WASI errors without accessing those resources
    And repeated calls still consume bounded fuel and deadline

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
      | math    |
      | wasm    |
