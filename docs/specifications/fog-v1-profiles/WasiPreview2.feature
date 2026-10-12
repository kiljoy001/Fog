@fog_v1_profiles @design
Feature: A WASM component runs as a Fog process and uses its namespace through WASI Preview 2
  A component is an application like any other: its parent forks it and execs it from
  /bin/{app}, which exec recognises as a component by its preamble. The component's WASI 0.2
  imports act on that process, through the same layer as Preview 1's: its descriptors, its
  namespace, its environment and its exit. Its descriptors are resources, each holding a
  process descriptor; its streams do not block, and it waits through pollables. The
  interfaces follow the WIT of WASI 0.2.12. These acceptance scenarios are pending production
  bindings.

  Background:
    Given a booted Fog kernel
    And "/bin/report" holds a WASM component

  @WASI_P2_001
  Scenario: A component is forked and execed like any program, and its parent waits for it
    Given a process that forks a child
    When the child execs "/bin/report" with the arguments "report" and "daily"
    Then the component's get-arguments returns "report" and "daily"
    And when its run returns ok the parent's wait returns a Waitmsg with an empty status

  @WASI_P2_002
  Scenario Outline: How the component ends is its exit status
    Given a process that forks a child
    When the child execs a component that <ends>
    Then the parent's wait returns a Waitmsg with the status "<status>"

    Examples:
      | ends                        | status                 |
      | returns ok from run         |                        |
      | returns err from run        | wasm-exit 1            |
      | calls exit with ok          |                        |
      | calls exit with err         | wasm-exit 1            |
      | calls exit-with-code with 0 |                        |
      | calls exit-with-code with 3 | wasm-exit 3            |
      | executes unreachable        | wasm-trap: unreachable |

  @WASI_P2_003
  Scenario: The component's environment is its process's environment
    Given a process whose environment holds "region" as "north"
    When it execs a component that calls get-environment and initial-cwd
    Then get-environment includes "region" as "north"
    And initial-cwd returns none

  @WASI_P2_004
  Scenario: Stdout is a stream on descriptor 1, whatever the parent left there
    Given a process that makes a pipe and forks a child with the pipe's first end as descriptor 1
    When the child execs a component that writes "hello" to get-stdout's stream and flushes it
    Then the parent reads "hello" from the pipe's second end

  @WASI_P2_005
  Scenario: Bytes read ahead from stdin belong to descriptor 0, so no stream loses them
    Given a process with a pipe's first end as descriptor 0
    And the pipe's second end has written "abcdef"
    When it execs a component that reads 2 bytes from get-stdin's stream and drops the stream
    And the component takes get-stdin's stream again and reads 10 bytes
    Then the first read returned "ab" and the second "cdef"

  @WASI_P2_006
  Scenario Outline: A terminal is offered only for a descriptor on the console
    Given a process with <what> as descriptor 0
    When it execs a component that calls get-terminal-stdin
    Then it returns <result>

    Examples:
      | what               | result     |
      | the console        | a terminal |
      | a pipe's first end | none       |
      | a file             | none       |

  @WASI_P2_007
  Scenario: The directories the parent left open are the preopens, and each comes as a duplicate
    Given a process with "/data" open as descriptor 3 and "/data/report" open as descriptor 4
    When it execs a component that calls get-directories
    Then it returns one descriptor, named "/data", on the directory descriptor 3 names
    And dropping that descriptor closes only its own process descriptor, and descriptor 3 stays open
    And calling get-directories again returns a new descriptor on "/data"

  @WASI_P2_008 @security @property
  Scenario Outline: A path cannot leave the directory it is resolved from
    Given a process with "/data" open as descriptor 3 and "/secret" in its namespace
    When the component calls open-at on the "/data" preopen with <path>
    Then it fails with not-permitted and nothing outside "/data" is walked or opened

    Examples:
      | path                                                         |
      | /secret                                                      |
      | ../secret                                                    |
      | child/../../secret                                           |
      | child/../../data/report                                      |
      | a name bound over "/secret" by a mount it does not authorize |

  @WASI_P2_009
  Scenario: A path may walk back up within its directory
    Given a process with "/data" open as descriptor 3 and "/data/report" holding report data
    When the component calls open-at on the "/data" preopen with "sub/../report"
    Then it opens "/data/report"

  @WASI_P2_010 @security
  Scenario Outline: A descriptor's flags are fixed when it is opened, and only narrow
    Given a process with "/data" open as descriptor 3
    And the component has opened "sub" beneath the "/data" preopen with only the read flag
    When it calls open-at on "sub" with <request>
    Then it fails with read-only before any provider changes anything

    Examples:
      | request                   |
      | the write flag            |
      | the mutate-directory flag |
      | the create open flag      |
      | the truncate open flag    |

  @WASI_P2_011 @security
  Scenario: Every flag a preopen has is still checked by the file server
    Given a process running as "glenda" with "/data" open as descriptor 3
    And "/data/locked" is a file "glenda" may not write
    When the component calls open-at on "locked" with the write flag
    Then it fails with access, as the file server refuses the open

  @WASI_P2_012
  Scenario Outline: Open flags keep their distinct meanings
    Given a process with "/data" open as descriptor 3 and "/data/report" holding report data
    When the component calls open-at on "report" with <flags>
    Then the result is <result>

    Examples:
      | flags             | result                                |
      | create            | open with existing contents           |
      | create, exclusive | exist with contents unchanged         |
      | truncate          | open with length zero                 |
      | directory         | not-directory with contents unchanged |

  @WASI_P2_013
  Scenario: Streams on a file read and write at their own offsets
    Given a component with "/data/report" open, holding "0123456789"
    When it takes a read stream at offset 2 and another at offset 6, and reads 3 bytes from each
    Then the first returns "234" and the second "678"
    When it takes a write stream at offset 4, writes "ab" and flushes it
    Then the file holds "0123ab6789"
    And the descriptor's read and write at an offset change no stream's offset

  @WASI_P2_014
  Scenario: A read on an empty pipe returns at once, and its pollable says when to read again
    Given a component with get-stdin's stream on an empty pipe
    When it calls read for 10 bytes
    Then the read returns an empty list at once
    And the stream's pollable is not ready
    When the pipe's other end writes "x"
    Then the stream's pollable becomes ready, and read returns "x"
    When the pipe's other end is closed
    Then the stream's pollable becomes ready, and read fails with closed

  @WASI_P2_015
  Scenario: A blocking read holds only the component's thread
    Given a component blocked in blocking-read on get-stdin's stream over an empty pipe
    Then other processes, the component's parent among them, keep running
    And when the pipe's other end writes "x", the blocking-read returns "x"

  @WASI_P2_016
  Scenario: An output stream accepts only what check-write permits
    Given a component with a write stream on a file
    When it calls check-write and gets a permit of n bytes
    Then a write of n bytes succeeds and a write of more than check-write permits traps
    And blocking-write-and-flush of up to 4096 bytes returns once they reach the file
    And blocking-write-and-flush of more than 4096 bytes traps

  @WASI_P2_017
  Scenario: A failed write is reported once, and the stream is closed after it
    Given a component with a write stream on a gefs file, and gefs full
    When it writes "x" and calls blocking-flush
    Then it fails with last-operation-failed, and filesystem-error-code gives insufficient-space
    And every later operation on the stream fails with closed

  @WASI_P2_018
  Scenario: poll waits on streams and clocks, and returns those that are ready
    Given a component with get-stdin's stream on an empty pipe
    When it polls the stream's pollable and a pollable from subscribe-duration of one second
    Then poll returns after one second with only the clock's index
    When it polls them again, and the pipe's other end writes "x"
    Then poll returns the stream's index
    And poll with an empty list traps

  @WASI_P2_019
  Scenario: read-directory leaves out "." and "..", and each stream starts again
    Given a process with a union directory holding repeated names open as descriptor 3
    When the component reads the preopen's directory through two streams, a few entries at a time from each
    Then each stream returns every entry in union order, repeated names included, and no "." or ".."
    And neither stream disturbs the other
    And no raw 9P stat record reaches the component

  @WASI_P2_020
  Scenario: stat converts the file's stat without inventing POSIX identity
    Given a component with "/data/report" open, last modified at 2026-10-01 12:00 UTC
    When it calls stat on the descriptor
    Then the type is regular-file, the size is the file's length and the link count is 1
    And the data-modification-timestamp is 2026-10-01 12:00 UTC
    And the status-change-timestamp is none

  @WASI_P2_021
  Scenario: Whether two descriptors are the same file, and whether a file changed, come from its qid
    Given a component with "/data/report" opened twice, and "/data/other" open
    Then is-same-object is true for the two on "/data/report" and false with "/data/other"
    When the component writes to "/data/report"
    Then its metadata-hash changes
    And the metadata-hash of a file nobody wrote does not change

  @WASI_P2_022
  Scenario Outline: What the namespace cannot honour fails explicitly
    Given a process with "/data" open as descriptor 3
    When the component calls <call>
    Then it fails with <error> and no copy, delete or emulation takes place

    Examples:
      | call                                                      | error        |
      | rename-at from "a" to "sub/a" beneath the "/data" preopen | cross-device |
      | symlink-at                                                | unsupported  |
      | link-at                                                   | unsupported  |
      | readlink-at                                               | unsupported  |
      | append-via-stream on a file that is not append-only       | unsupported  |
      | set-times setting the data access timestamp               | unsupported  |
      | sync on a provider without a durability guarantee         | unsupported  |

  @WASI_P2_023
  Scenario: rename-at within one directory renames by wstat
    Given a process with "/data" open as descriptor 3 and "/data/a" holding "x"
    When the component calls rename-at from "a" to "b" beneath the "/data" preopen
    Then "/data/b" holds "x" and "/data/a" does not exist

  @WASI_P2_024
  Scenario: append-via-stream appends to an append-only file
    Given a component with "/data/log", an append-only file holding "one", open
    When it takes an append stream, writes "two" and flushes it
    Then "/data/log" holds "onetwo"

  @WASI_P2_025
  Scenario: sync on gefs commits what the component wrote
    Given a component with a file on gefs open with the write flag
    When it writes "kept" through a stream, flushes it and calls sync
    Then gefs has committed, and "kept" is in the file after the store is opened again

  @WASI_P2_026
  Scenario Outline: Kernel errors reach the component as filesystem error codes
    Given a process with "/data" open as descriptor 3
    When the component calls <call> and the kernel fails with "<error>"
    Then the call returns <code>

    Examples:
      | call                                       | error                              | code               |
      | open-at on "missing"                       | file does not exist                | no-entry           |
      | open-at on "report" with create, exclusive | file already exists                | exist              |
      | open-at on "locked" with the write flag    | permission denied                  | access             |
      | unlink-file-at on "sub"                    | file is a directory                | is-directory       |
      | remove-directory-at on "report"            | not a directory                    | not-directory      |
      | remove-directory-at on "full"              | directory is not empty             | not-empty          |
      | write on a file in "/data"                 | file system full                   | insufficient-space |
      | create-directory-at on "union/new"         | mounted directory forbids creation | read-only          |
      | read on a file in "/data"                  | i/o timeout                        | io                 |

  @WASI_P2_027
  Scenario Outline: Clocks and randomness come from the host and grant nothing else
    Given a component
    When it calls <call>
    Then <result>

    Examples:
      | call                                   | result                                                        |
      | monotonic-clock now twice              | the second instant is not before the first                    |
      | wall-clock now                         | it returns the host's time, with nanoseconds below 1000000000 |
      | get-random-bytes for 32 bytes twice    | the two results differ                                        |
      | get-insecure-random-bytes for 32 bytes | it returns 32 bytes                                           |
      | insecure-seed                          | it returns two u64 values                                     |

  @WASI_P2_028
  Scenario Outline: The component links sockets, but Fog has no network device to give it
    Given a component
    When it calls <call>
    Then it fails with not-supported

    Examples:
      | call                                                              |
      | create-tcp-socket for IPv4                                        |
      | create-udp-socket for IPv6                                        |
      | resolve-addresses for "example.org" on instance-network's network |

  @WASI_P2_029
  Scenario: A trap ends the process without rolling back what it wrote
    Given a process that forks a child holding "/tmp/scratch" open ORCLOSE
    When the child execs a component that writes "partial" to "/data/out", flushes it, and then traps
    Then the parent's wait returns a Waitmsg whose status starts "wasm-trap: "
    And "/data/out" holds "partial"
    And "/tmp/scratch" has been removed, as when any process exits

  @WASI_P2_030 @coyote
  Scenario: Read-ahead finishing after its stream, descriptor or process is gone writes nothing
    Given a component with a read stream on a pipe, its read-ahead waiting on the pipe
    When the component drops the stream, or the descriptor, or the process exits, as the pipe's other end writes "x"
    Then the read-ahead's bytes go to the descriptor if it is still open, and nowhere otherwise
    And nothing is written into guest memory that has been disposed
