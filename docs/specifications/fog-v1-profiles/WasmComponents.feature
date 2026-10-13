@fog_v1_profiles @design
Feature: Exec admits WASI 0.2 components and calls them through the canonical ABI
  dotnet-webassembly runs core modules only, so Fog carries the component layer itself: it
  parses and validates the component, instantiates its core modules with the engine, links
  them as the component says, and lifts and lowers every value crossing an import or export
  by the canonical ABI. The profile is the component model at 6c48079 without any of its
  gated features, which is what WASI 0.2 shipped, and the WASI 0.2.12 interfaces listed in
  Wasm.md. These acceptance scenarios are pending production bindings.

  Background:
    Given a booted Fog kernel

  @WASM_CM_001
  Scenario Outline: Exec tells a core module from a component by its preamble
    Given "/bin/app" holds <form>
    When a process execs "/bin/app"
    Then <result>

    Examples:
      | form                                              | result                                    |
      | a core module, preamble 00 61 73 6d 01 00 00 00   | it runs with WASI Preview 1               |
      | a component, preamble 00 61 73 6d 0d 00 01 00     | it runs with WASI Preview 2               |
      | the WASM magic with version 0d 00 and layer 00 00 | the exec fails with "exec header invalid" |
      | the WASM magic with version 02 00 00 00           | the exec fails with "exec header invalid" |

  @WASM_CM_002
  Scenario Outline: Exec refuses a component outside the profile and leaves the process as it was
    Given a process with "/tmp/f" open as descriptor 3
    When it execs a component <defect>
    Then the exec fails with "exec header invalid"
    And the process still runs its own program with descriptor 3 open

    Examples:
      | defect                                                                     |
      | importing "wasi:http/outgoing-handler@0.2.12"                              |
      | importing "wasi:clocks/timezone@0.2.12", which 0.2.12 marks unstable       |
      | importing "wasi:filesystem/types@0.3.0"                                    |
      | importing "wasi:cli/environment@0.2.13"                                    |
      | importing exit-with-code from "wasi:cli/exit@0.2.11"                       |
      | importing "wasi:filesystem/types@0.2.12" with a function of the wrong type |
      | not exporting "wasi:cli/run"                                               |
      | exporting run with a type other than func() -> result                      |
      | lifting a function async                                                   |
      | using a stream or future type                                              |
      | using a map type                                                           |
      | using the error-context type                                               |
      | with a value import                                                        |
      | with a component start function                                            |
      | using a fixed-length list                                                  |
      | holding a core module with a start section                                 |
      | holding a core module with a shared memory                                 |
      | holding a core module that imports "wasi_snapshot_preview1"                |

  @WASM_CM_003
  Scenario: Imports at any 0.2 version up to 0.2.12 link together
    Given a component importing "wasi:io/streams@0.2.0", "wasi:filesystem/types@0.2.3" and "wasi:cli/exit@0.2.12"
    When a process execs it
    Then the component runs, and calls through each import reach the same kernel calls

  @WASM_CM_004 @property @fuzz
  Scenario: Every string from the guest is checked for its encoding before any kernel call
    Given components using each string encoding the canonical ABI allows: UTF-8, UTF-16 and Latin-1+UTF-16
    And generated path strings, valid and invalid in each encoding
    When each component passes them to open-at
    Then a valid string reaches the kernel as the same characters, whatever its encoding
    And an invalid sequence or a surrogate code point traps, and no kernel call is made

  @WASM_CM_005 @property @fuzz
  Scenario: Every pointer and length from the guest is checked before any kernel call
    Given generated pointers and lengths for the strings and lists passed to write and open-at
    When the canonical ABI lifts them
    Then a misaligned pointer, or a range past the end of memory, traps before any kernel call
    And a string longer than MAX_STRING_BYTE_LENGTH or a list longer than MAX_LIST_BYTE_LENGTH traps
    And no range wraps, as the arithmetic is widened

  @WASM_CM_006 @security
  Scenario Outline: The component's realloc is checked like any other pointer
    Given a component whose realloc <misbehaves>
    When it calls read on a file, and the result is lowered through realloc
    Then the component traps, and no byte is written outside its memory

    Examples:
      | misbehaves                                |
      | returns a pointer past the end of memory  |
      | returns a pointer misaligned for the list |
      | calls an import                           |

  @WASM_CM_007
  Scenario: post-return runs after the results are read, and may not call an import
    Given a component whose export's post-return calls an import
    When the kernel calls the component's run, and run returns
    Then the post-return traps, and the process ends with a "wasm-trap: " status

  @WASM_CM_008 @security
  Scenario Outline: Handles are checked on every use
    Given a component holding a descriptor and a stream taken from it
    When it <does>
    Then the component traps, and no kernel call is made for it

    Examples:
      | does                                                         |
      | calls stat on a descriptor it has already dropped            |
      | passes a stream's handle where a descriptor is expected      |
      | passes a handle index that names nothing                     |
      | drops the descriptor while the stream taken from it is alive |
      | drops a stream while a pollable from its subscribe is alive  |

  @WASM_CM_009
  Scenario: A borrowed handle is the host's only for the call
    Given a component holding descriptors on "/data" and "/data/sub"
    When it calls rename-at from "a" to "b", lending the "/data" descriptor as the new directory
    Then after the call returns the component can drop both descriptors
    And the host holds no handle of the component's once the call has returned

  @WASM_CM_010
  Scenario: The handle table is bounded
    Given a component with room for 1000 handles
    When it takes pollables from subscribe-duration until it has 1001 handles
    Then the 1001st handle traps, and the process ends with a "wasm-trap: " status

  @WASM_CM_011 @security
  Scenario: Core modules inside a component share only what the component links
    Given a component holding two core modules, each with its own memory, linked through a function of the first
    When the second calls it with a list
    Then the list is copied between the memories by the canonical ABI
    And neither module's memory is reachable from the other

  @WASM_CM_012
  Scenario: Every lowered import call is metered
    Given a component with a host-call limit of 10 and an IO limit of 100 bytes
    When it calls write with 64 bytes and then with 64 bytes more
    Then the second write ends the process with status io-limit before the kernel call
    And a component making 11 calls of any import ends with status host-call-limit
