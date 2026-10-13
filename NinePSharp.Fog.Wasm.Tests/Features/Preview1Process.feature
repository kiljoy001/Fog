@fog_wasm_preview1
Feature: A WASI Preview 1 core module runs as a Fog process
  exec recognises a core module by its preamble and offers it to the WASM loader, which admits only
  a module inside the profile: one 32-bit memory of its own, exported as "memory",
  no start section, an exported _start of no parameters and no results, and imports only of
  wasi_snapshot_preview1 functions with exactly the types the Preview 1 WITX at a2b96e81 gives them.
  A module that cannot be read, checked, compiled or set up is refused: exec fails with "exec header
  invalid" and leaves the process running its own program. The module is set up, its data and
  elements placed, before exec commits. An admitted module runs on a thread of its own, and its imports act on its process: the
  argv exec was given, the process's environment, and its descriptors. How the application ends is
  its exit status: nothing when _start returns or proc_exit is given 0, "wasm-exit N" for any other
  code, and "wasm-trap: " and the trap otherwise, named as WASI runtimes name it where the engine's
  exception says which trap it was. A kernel call that ends the process ends it as it would any
  process. A function of the profile that Fog does not provide
  returns NOSYS. Guest memory is checked before any kernel call; an access outside it is FAULT. A
  memory may grow to the maximum the module declares, or without one to WebAssembly's own limit for a
  32-bit memory, 65536 pages, and a host call reaches all of it; one call copies at most a mebibyte,
  so a guest cannot make the host allocate without bound. A vector of buffers or of subscriptions
  longer than that is INVAL, as Linux refuses more than IOV_MAX buffers, and a path longer than that
  is NAMETOOLONG, as Linux refuses one past PATH_MAX; one that runs outside memory is FAULT first.

  Background:
    Given a kernel that runs WASM applications

  @WASI_NS_001
  Scenario: An application is forked and execed like any program, and its parent waits for it
    Given "/bin/report" holds the application "echo-args"
    When a child with a pipe as descriptor 1 execs "/bin/report" with the arguments "report daily"
    Then the pipe holds "report\ndaily\n"
    And the parent's wait message for it is empty

  @WASI_NS_001
  Scenario: args_sizes_get counts each argument with its NUL
    Given "/bin/report" holds the application "args-sizes"
    When a child with a pipe as descriptor 1 execs "/bin/report" with the arguments "report daily"
    Then the parent's wait message for it is "report <pid>: wasm-exit 13"

  @WASI_NS_001
  Scenario: args_get puts each argument, NUL-terminated, right after the one before
    Given "/bin/report" holds the application "args-buffer"
    When a child with a pipe as descriptor 1 execs "/bin/report" with the arguments "report daily"
    Then the pipe holds "report\0daily\0"

  @WASI_NS_001
  Scenario: A #! script may name an application as its interpreter
    Given "/bin/hello" holds the application "hello"
    And "/bin/greet" holds "#!/bin/hello\n"
    When a child with a pipe as descriptor 1 execs "/bin/greet"
    Then the pipe holds "hello"

  @WASI_NS_015
  Scenario: Descriptor 1 is the one the parent left open
    Given "/bin/hello" holds the application "hello"
    When a child with a pipe as descriptor 1 execs "/bin/hello"
    Then the pipe holds "hello"

  @WASI_NS_015
  Scenario Outline: The application holds no descriptor its parent did not leave open
    Given "/bin/report" holds the application "<fixture>"
    And the process's environment holds "region" as "north"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: wasm-exit 8"

    Examples:
      | fixture    |
      | write-fd-0 |
      | write-fd-2 |

  @WASI_NS_017
  Scenario Outline: Exec refuses a module outside the profile and leaves the process as it was
    Given "/bin/bad" holds the application "<fixture>"
    When a child with "/tmp/f" open as descriptor 3 execs "/bin/bad"
    Then exec fails with "exec header invalid"
    And the child still has descriptor 3 open and its own name

    Examples:
      | fixture           | defect                                               |
      | start-section     | with a start section                                 |
      | shared-memory     | with a shared memory                                 |
      | imported-memory   | with an imported memory                              |
      | no-memory-export  | without a memory exported as "memory"                |
      | memory64          | with a 64-bit memory                                 |
      | no-start          | without an exported _start                           |
      | start-with-result | with a _start that returns a value                   |
      | unknown-function  | importing a wasi_snapshot_preview1 function it lacks |
      | other-module      | importing from a module other than Preview 1's       |
      | wrong-type        | importing a profile function with the wrong type     |
      | wrong-result      | importing a profile function with the wrong result   |
      | imported-global   | importing a global                                   |
      | start-with-param  | with a _start that takes a parameter                 |

  @WASI_NS_017
  Scenario Outline: Exec refuses a module it cannot set up and leaves the process as it was
    Given "/bin/bad" holds the application "<fixture>"
    When a child with "/tmp/f" open as descriptor 3 execs "/bin/bad"
    Then exec fails with "exec header invalid"
    And the child still has descriptor 3 open and its own name

    Examples:
      | fixture         | defect                                 |
      | data-outside    | with data that does not fit its memory |
      | element-outside | with elements that do not fit its table |

  @WASI_NS_017 @property @fuzz
  Scenario: A module with any byte changed is either admitted or refused, and fails no other way
    Given the applications the other scenarios admit
    When any one byte of one of them is changed
    Then loading it either gives a program or fails with "exec header invalid"

  @WASI_NS_017
  Scenario: A module may import every Preview 1 function with the type its WITX gives it
    Given "/bin/report" holds the application "all-functions"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is empty

  @WASI_NS_017
  Scenario: A module may import the same function more than once
    Given "/bin/report" holds the application "imports-twice"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the pipe holds "hello"

  @WASI_NS_017
  Scenario Outline: A memory may have any maximum a 32-bit memory can, or none
    Given "/bin/report" holds the application "<fixture>"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is empty

    Examples:
      | fixture        | memory                                                       |
      | largest-memory | with a maximum of 65536 pages, 4 GiB                         |
      | no-maximum     | without a maximum, which cannot grow by 65536 pages from one |

  @WASI_NS_017
  Scenario: A host call given more than 2 GiB - 1 bytes at once is FAULT, even inside memory
    Given "/bin/report" holds the application "huge-random"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: wasm-exit 21"

  @WASI_NS_007
  Scenario Outline: One call copies at most a mebibyte of what it is given out of guest memory
    Given "/bin/report" holds the application "<fixture>"
    And the directory "/data"
    When a child with "/data" open for reading as descriptor 3 and a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "<message>"

    Examples:
      | fixture          | message                     | what                                              |
      | poll-most        |                             | 21845 subscriptions, the most that fit            |
      | poll-too-many    | report <pid>: wasm-exit 28  | 21846 subscriptions, INVAL                        |
      | poll-count-huge  | report <pid>: wasm-exit 21  | 2^31 subscriptions, outside memory, FAULT         |
      | iovs-most        |                             | 131072 buffers, the most that fit                 |
      | iovs-too-many    | report <pid>: wasm-exit 28  | 131073 buffers, INVAL                             |
      | iovs-count-huge  | report <pid>: wasm-exit 21  | 2^31 buffers, outside memory, FAULT               |
      | path-longest     | report <pid>: wasm-exit 37  | a mebibyte path, which namec finds too long       |
      | path-too-long    | report <pid>: wasm-exit 37  | a mebibyte and a byte, NAMETOOLONG                |
      | path-length-huge | report <pid>: wasm-exit 21  | 2^31 - 1 bytes, outside memory, FAULT             |
      | path-longest-invalid  | report <pid>: wasm-exit 25 | a mebibyte of 0xff, copied and not UTF-8, ILSEQ |
      | path-too-long-invalid | report <pid>: wasm-exit 37 | a mebibyte and a byte of 0xff, never copied     |

  @WASI_NS_017
  Scenario: A host call reaches guest memory above 2 GiB
    Given "/bin/report" holds the application "high-memory"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the pipe holds "high"

  @WASI_NS_017
  Scenario Outline: Exec refuses a file that only starts like a core module
    Given "/bin/bad" holds the bytes <bytes>
    When a child with "/tmp/f" open as descriptor 3 execs "/bin/bad"
    Then exec fails with "exec header invalid"
    And the child still has descriptor 3 open and its own name

    Examples:
      | bytes                               | what                       |
      | 00 61 73 6d 02 00 00 00             | another version            |
      | 00 61 73 6d 01 00 00 00 05 03 01 00 | a truncated memory section |

  @WASI_NS_018
  Scenario Outline: How the application ends is its exit status
    Given "/bin/report" holds the application "<fixture>"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "<message>"

    Examples:
      | fixture        | message                                         |
      | return         |                                                 |
      | exit-0         |                                                 |
      | exit-3         | report <pid>: wasm-exit 3                       |
      | unreachable    | report <pid>: wasm-trap: unreachable            |
      | out-of-bounds  | report <pid>: wasm-trap: out of bounds memory access |
      | divide-by-zero | report <pid>: wasm-trap: integer divide by zero |
      | integer-overflow | report <pid>: wasm-trap: integer overflow |
      | undefined-element      | report <pid>: wasm-trap: out of bounds table access |
      | table-out-of-bounds    | report <pid>: wasm-trap: out of bounds table access |
      | uninitialized-element  | report <pid>: wasm-trap: uninitialized element      |
      | indirect-type-mismatch | report <pid>: wasm-trap: indirect call type mismatch |

  @WASI_NS_019
  Scenario: The application's environment is its process's environment
    Given "/bin/report" holds the application "environ"
    And the process's environment holds "region" as "north"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the pipe holds "region=north\n"

  @WASI_NS_022
  Scenario: fd_write on a descriptor that is not open is BADF
    Given "/bin/report" holds the application "write-fd-9"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: wasm-exit 8"

  @WASI_NS_022
  Scenario: Any other kernel error is IO
    Given "/bin/report" holds the application "write-closed"
    When a child with "/tmp/f" open for reading as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: wasm-exit 29"

  @WASI_NS_011
  Scenario: A kernel call that ends the process ends the application as it would any process
    Given "/bin/report" holds the application "write-closed"
    When a child with a pipe whose other end is closed as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: sys: write on closed pipe"

  @WASI_NS_007
  Scenario Outline: Guest memory is checked before any kernel call
    Given "/bin/report" holds the application "<fixture>"
    When a child with a pipe as descriptor 1 execs "/bin/report" with the arguments "report daily"
    Then the parent's wait message for it is "report <pid>: wasm-exit 21"
    And the pipe holds ""

    Examples:
      | fixture          | what lies outside memory   |
      | iovs-outside     | the vector of buffers      |
      | buffer-outside   | a buffer the vector names  |
      | nwritten-outside | where the count is written |
      | args-count-outside  | where args_sizes_get writes the count       |
      | args-size-outside   | where args_sizes_get writes the buffer size |
      | args-list-outside   | where args_get writes the pointers          |
      | args-buffer-outside | where args_get writes the strings           |
      | read-buffer-outside   | a buffer fd_read fills                    |
      | read-count-outside    | where fd_read writes the count            |

  @WASI_NS_007
  Scenario: A call whose result cannot be written is FAULT and leaves the descriptor as it was
    Given "/bin/report" holds the application "seek-position-outside"
    And the file "/tmp/f" holds "0123456789"
    When a child with "/tmp/f" open for reading as descriptor 3 and a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: wasm-exit 21"
    And the pipe holds "0"

  @WASI_NS_007
  Scenario: path_open whose descriptor cannot be written is FAULT and creates nothing
    Given "/bin/report" holds the application "open-result-outside"
    And the directory "/data"
    When a child with "/data" open for reading as descriptor 3 and a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: wasm-exit 21"
    And "/data/new" does not exist

  @WASI_NS_007
  Scenario: A buffer may end at the last byte of memory
    Given "/bin/report" holds the application "buffer-at-end"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the pipe holds "hello"

  @WASI_NS_007
  Scenario: One fd_write copies at most a mebibyte and reports the short write
    Given "/bin/report" holds the application "write-big"
    When a child with "/dev/null" open as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: wasm-exit 1048576"

  @WASI_NS_007
  Scenario: Every buffer is checked, even past the mebibyte one fd_write copies
    Given "/bin/report" holds the application "outside-past-limit"
    When a child with "/dev/null" open as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: wasm-exit 21"

  @WASI_NS_013
  Scenario: proc_raise is NOSYS
    Given "/bin/report" holds the application "raise"
    When a child with a pipe as descriptor 1 execs "/bin/report"
    Then the parent's wait message for it is "report <pid>: wasm-exit 52"
