@fog_v1_profiles @design @gate
Feature: Fog's WASI is compared against reference runtimes as a control
  The same WASM programs run under Fog and under pinned reference runtimes, and what each
  does is compared. Wasmtime is the control for both previews. Wasmtime builds its Preview 1
  on its Preview 2, so each preview has a second, independent control: wazero for Preview 1
  and jco for Preview 2. Where the two controls agree, Fog must agree with them, unless the
  divergence ledger names the difference and the scenario that states it. Where they
  disagree, the behaviour is left to the implementation, and Fog is held to its own
  scenarios instead. The comparison runs on the gate. These acceptance scenarios are
  pending production bindings.

  Background:
    Given Wasmtime 49.0.2, wazero 1.12.0 and jco 1.37.0, each pinned by its release hash
    And the trace interpreter, built for Preview 1 as a core module and for Preview 2 as a component
    And the starting tree: "/data" holding "report" with "0123456789", an empty directory "sub", and "full" holding "x"

  @WASI_DIFF_001
  Scenario: A control that is not the pinned build stops the run before anything is compared
    Given the installed Wasmtime's hash differs from the pinned one
    When the differential run starts
    Then it fails naming Wasmtime and both hashes
    And no comparison is reported as passing

  @WASI_DIFF_002
  Scenario Outline: The trace interpreter is an application every host admits
    When the <preview> interpreter is given to Fog, Wasmtime and <second>
    Then each host runs it
    And it imports only <imports>

    Examples:
      | preview   | second | imports                                                |
      | Preview 1 | wazero | functions of wasi_snapshot_preview1                    |
      | Preview 2 | jco    | interfaces Fog links, at versions from 0.2.0 to 0.2.12 |

  @WASI_DIFF_003
  Scenario Outline: The same script gives the same trace under every host
    Given a script that opens "report" beneath the "/data" preopen, reads 4 bytes at offset 2, writes "ab" at offset 8, and stats it
    When the <preview> interpreter runs the script under Fog, Wasmtime and <second>
    Then the three traces are the same after normalising
    And the three final trees are the same

    Examples:
      | preview   | second |
      | Preview 1 | wazero |
      | Preview 2 | jco    |

  @WASI_DIFF_004 @property
  Scenario Outline: Generated call sequences behave as the controls do
    Given generated scripts of calls on the starting tree: open, read, write, seek, readdir, stat, set-size, rename, create and remove directory, unlink and close
    When each script runs under Fog, Wasmtime and <second> with the <preview> interpreter
    Then every trace and final tree Fog gives matches the controls' wherever the controls agree
    And a script on which Fog differs, with no ledger entry for it, fails the run
    And it is reported shrunk to the shortest script that still differs, with each host's trace

    Examples:
      | preview   | second |
      | Preview 1 | wazero |
      | Preview 2 | jco    |

  @WASI_DIFF_005
  Scenario: Where the controls disagree, the behaviour is the implementation's, and Fog is held to its own scenarios
    Given a script whose call returns one error under Wasmtime and another under wazero
    When the differential run compares it
    Then the call is recorded as left to the implementation, with both controls' results
    And Fog's result for it is not compared with either
    And the scenario in the WASI features that covers the call decides what Fog must do

  @WASI_DIFF_006
  Scenario Outline: What legitimately differs between hosts is normalised before comparing
    Given traces from two hosts that differ only in <value>
    When they are normalised
    Then <normalised>

    Examples:
      | value                                        | normalised                                                                     |
      | inode numbers                                | each is replaced by the order it was first seen in, so equal files still match |
      | access, modification and status-change times | each is replaced by whether it moved forward since the last stat of the file   |
      | readdir cookies                              | each is replaced by the entry it continues from                                |
      | metadata hashes                              | each is replaced by whether it changed since the last hash of the file         |
      | random bytes                                 | only their length is compared                                                  |
      | clock values                                 | only that each monotonic reading is not before the last is compared            |
      | the order of entries in a plain directory    | entries are compared sorted by name                                            |
      | the text of an error, trap or refusal        | only its kind is compared: errno or error code, trap or refusal                |

  @WASI_DIFF_007
  Scenario: A union directory keeps Fog's own order, which has no control
    Given a script that reads a union directory holding repeated names
    When the differential run compares it
    Then the call is left out of the comparison, as no control has union directories
    And WASI_NS_010 and WASI_P2_019 decide what Fog must do

  @WASI_DIFF_008
  Scenario Outline: A divergence the ledger names passes only as its scenario says
    Given a script that calls <call>
    When it runs under Fog and the controls
    Then Fog's result differs from the controls' as the ledger entry says
    And Fog's result is what <scenario> says

    Examples:
      | call                                                      | scenario    |
      | path_symlink                                              | WASI_NS_013 |
      | symlink-at                                                | WASI_P2_022 |
      | path_link                                                 | WASI_NS_013 |
      | link-at                                                   | WASI_P2_022 |
      | path_rename from "a" to "sub/a"                           | WASI_NS_013 |
      | rename-at from "a" to "sub/a"                             | WASI_P2_022 |
      | readlink-at                                               | WASI_P2_022 |
      | set-times setting the data access timestamp               | WASI_P2_022 |
      | append-via-stream on a file that is not append-only       | WASI_P2_022 |
      | fd_fdstat_get on the "/data" preopen                      | WASI_NS_004 |
      | fd_readdir on the "/data" preopen, reading its ".." entry | WASI_NS_010 |
      | initial-cwd                                               | WASI_P2_003 |
      | clock_time_get on the process CPU-time clock              | WASI_NS_024 |
      | sock_accept on a descriptor                               | WASI_NS_025 |
      | create-tcp-socket                                         | WASI_P2_028 |

  @WASI_DIFF_009
  Scenario Outline: The ledger cannot go stale or name what does not exist
    Given a ledger entry <defect>
    When the differential run checks the ledger
    Then the run fails naming the entry

    Examples:
      | defect                                                    |
      | for a call on which Fog now behaves as the controls do    |
      | naming a scenario tag that no WASI feature holds          |
      | for a call on which the controls disagree with each other |

  @WASI_DIFF_010
  Scenario: The Preview 1 tests of wasi-testsuite give the same results under Fog and the controls
    Given wasi-testsuite at 5ea11d8
    When its Preview 1 tests run under Fog, Wasmtime and wazero
    Then each test passes or fails under Fog as it does under both controls
    And a test where Fog differs is named in the ledger with the scenario that states why

  @WASI_DIFF_011
  Scenario Outline: Fixture applications end the same way, with the same output and files
    Given the fixture application <fixture>, built for <preview> with its pinned toolchain
    When it runs under Fog, Wasmtime and <second> with the same arguments, environment and stdin
    Then its stdout, stderr, exit status and final tree are the same under all three

    Examples:
      | fixture                                     | preview   | second |
      | copying stdin to stdout                     | Preview 1 | wazero |
      | copying stdin to stdout                     | Preview 2 | jco    |
      | listing the "/data" preopen                 | Preview 1 | wazero |
      | listing the "/data" preopen                 | Preview 2 | jco    |
      | writing a report into "/data" and exiting 3 | Preview 1 | wazero |
      | writing a report into "/data" and exiting 3 | Preview 2 | jco    |

  @WASI_DIFF_012
  Scenario: Malformed calls trap or fail under Fog where they do under the controls
    Given generated calls with out-of-bounds pointers, misaligned pointers, invalid strings and dead handles
    When each runs under Fog and the controls
    Then wherever both controls trap, Fog traps too, and wherever both fail the call with an error, Fog fails it with the same kind
    And only whether it trapped and the error's kind are compared, not the text

  @WASI_DIFF_013
  Scenario: Fog refuses more than the controls, and only as its profile says
    Given modules and components that the controls run but Fog's profile refuses: a core start section, an import of wasi:http
    When each is execed under Fog and run under the controls
    Then Fog refuses each with "exec header invalid"
    And each refusal is a ledger entry naming WASI_NS_017 or WASM_CM_002

  @WASI_DIFF_014
  Scenario: A script that does not repeat itself under one host is not compared
    Given a script whose trace differs between two runs under the same host
    When the differential run compares it
    Then the script is reported as nondeterministic, with both traces, and the run fails
    And the generator is fixed to stop producing such scripts, rather than the script being skipped
