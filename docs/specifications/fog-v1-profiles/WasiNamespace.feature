@fog_v1_profiles @design
Feature: A WASM application runs as a Fog process and uses its namespace through WASI Preview 1
  Each application is one Fog kernel process: its parent forks it and execs the module at
  /bin/{app}, which exec recognises as a core module by its preamble. The module's WASI
  Preview 1 imports act on that process: its descriptors, its namespace, its environment and
  its exit. The descriptors it starts with are the ones its parent left open, and the
  directories among them are its preopens. dotnet-webassembly provides the execution engine.
  Preview 2 components use the same process, paths, rights and errors through the same layer;
  WasiPreview2.feature and WasmComponents.feature describe what differs. The functions follow
  the Preview 1 WITX at a2b96e81. These acceptance scenarios are pending production bindings.

  Background:
    Given a booted Fog kernel
    And "/bin/report" holds a WASM application

  @WASI_NS_001
  Scenario: An application is forked and execed like any program, and its parent waits for it
    Given a process that forks a child
    When the child execs "/bin/report" with the arguments "report" and "daily"
    Then the application's args_get returns "report" and "daily"
    And when it returns from _start the parent's wait returns a Waitmsg with an empty status

  @WASI_NS_015
  Scenario: Descriptors 0, 1 and 2 are the ones the parent left open
    Given a process that makes a pipe and forks a child with the pipe's first end as descriptor 1
    When the child execs an application that writes "hello" to descriptor 1
    Then the parent reads "hello" from the pipe's second end

  @WASI_NS_016
  Scenario: The directories the parent left open are the preopens, under the names they were opened by
    Given a process with "/data" open as descriptor 3 and "/data/report" open as descriptor 4
    When it execs "/bin/report"
    Then fd_prestat_get reports descriptor 3 as a preopened directory named "/data"
    And fd_prestat_get reports descriptor 4 as not a preopen
    And no other descriptor is a preopen

  @WASI_NS_017
  Scenario Outline: Exec refuses a module outside the profile and leaves the process as it was
    Given a process with "/tmp/f" open as descriptor 3
    When it execs a module <defect>
    Then the exec fails with "exec header invalid"
    And the process still runs its own program with descriptor 3 open

    Examples:
      | defect                                           |
      | with a start section                             |
      | with a shared memory                             |
      | without a memory exported as "memory"            |
      | with no finite memory maximum                    |
      | importing a function outside the profile         |
      | importing a profile function with the wrong type |

  @WASI_NS_018
  Scenario Outline: How the application ends is its exit status
    Given a process that forks a child
    When the child execs an application that <ends>
    Then the parent's wait returns a Waitmsg with the status "<status>"

    Examples:
      | ends                   | status                 |
      | returns from _start    |                        |
      | calls proc_exit with 0 |                        |
      | calls proc_exit with 3 | wasm-exit 3            |
      | executes unreachable   | wasm-trap: unreachable |

  @WASI_NS_019
  Scenario: The application's environment is its process's environment
    Given a process whose environment holds "region" as "north"
    When it execs an application that reads its environment with environ_get
    Then the application sees "region=north"

  @WASI_NS_002 @security @property
  Scenario Outline: A path cannot leave the directory it is resolved from
    Given a process with "/data" open as descriptor 3 and "/secret" in its namespace
    When the application calls path_open on descriptor 3 with <path>
    Then it fails with NOTCAPABLE and nothing outside "/data" is walked or opened

    Examples:
      | path                                                         |
      | /secret                                                      |
      | ../secret                                                    |
      | child/../../secret                                           |
      | child/../../data/report                                      |
      | a name bound over "/secret" by a mount it does not authorize |

  @WASI_NS_003 @security
  Scenario Outline: A directory the application holds stays the one it was given
    Given an application holding descriptor 3 on "/data"
    When a process sharing its namespace <change>
    Then path_open of "report" beneath descriptor 3 <result>

    Examples:
      | change                                      | result                                  |
      | binds "/n/extra" over "/data/report"        | opens the file "/n/extra" names         |
      | binds "/secret" over "/data"                | fails with NOENT and opens nothing      |
      | removes "/data" and makes a new "/data"     | fails with NOENT and opens nothing      |
      | renames "/data" to "/old"                   | fails with NOENT and opens nothing      |

  @WASI_NS_003
  Scenario: A directory the application renames stays usable under its new name
    Given an application holding descriptor 4 on "/data/sub"
    When it renames "sub" to "moved" with path_rename beneath descriptor 3 on "/data"
    Then path_open of "f" beneath descriptor 4 opens "/data/moved/f"

  @WASI_NS_004 @property
  Scenario: A preopen starts with every right, and rights only narrow
    Given a process with "/data" open as descriptor 3
    When it execs an application
    Then fd_fdstat_get reports every right on descriptor 3
    When the application narrows descriptor 3 to reading with fd_fdstat_set_rights
    Then asking path_open or fd_fdstat_set_rights for write rights beneath it fails with NOTCAPABLE before any provider changes anything
    And no call restores the rights it gave up

  @WASI_NS_020 @security
  Scenario: Every right a preopen has is still checked by the file server
    Given a process running as "glenda" with "/data" open as descriptor 3
    And "/data/locked" is a file "glenda" may not write
    When the application calls path_open on "locked" for writing
    Then it fails with ACCES, as the file server refuses the open

  @WASI_NS_021
  Scenario: A path may walk back up within its directory
    Given a process with "/data" open as descriptor 3 and "/data/report" holding report data
    When the application calls path_open on descriptor 3 with "sub/../report"
    Then it opens "/data/report"

  @WASI_NS_005
  Scenario Outline: WASI open flags keep their distinct meanings
    Given a process with "/data" open as descriptor 3 and "/data/report" holding report data
    When the application calls path_open on "report" with <flags>
    Then the result is <result>

    Examples:
      | flags       | result                         |
      | CREATE      | open with existing contents    |
      | CREATE EXCL | EXIST with contents unchanged  |
      | TRUNC       | open with length zero          |
      | DIRECTORY   | NOTDIR with contents unchanged |

  @WASI_NS_006
  Scenario: fd_renumber is the process's renumber
    Given an application with two descriptors on different files, with different offsets
    When it calls fd_renumber to move the first onto the second
    Then the second number names the first file at the first offset
    And the first number is not open
    And the file the second number named is closed exactly once
    And renumbering a descriptor onto its own number changes nothing

  @WASI_NS_007 @property @fuzz
  Scenario: Guest memory is checked before any kernel call
    Given generated iovec tables, byte ranges, counts and result pointers
    When fd_read, fd_write, fd_pread or fd_pwrite validates them
    Then wrapped or out-of-bounds ranges fail before any kernel call
    And vector count and total copy size cannot exceed the admitted limits
    And valid IO reports the actual byte counts, including short transfers and end of file

  @WASI_NS_008
  Scenario: Memory growth cannot invalidate the host's next buffer access
    Given an application whose linear memory grows between file calls
    When the next call uses a buffer on a newly allocated page
    Then the bridge reads the current memory base and validates the new range
    And no pointer or Span from a previous call survives the kernel call

  @WASI_NS_009
  Scenario Outline: Positioned IO cannot reach Plan 9's implicit-offset sentinel
    Given an application with a file open as descriptor 3 at offset seven
    When it calls <call> with the offset <offset>
    Then it fails with an unrepresentable offset error without a kernel call
    And descriptor 3's offset is still seven

    Examples:
      | call      | offset               |
      | fd_pread  | 18446744073709551615 |
      | fd_pwrite | 9223372036854775808  |

  @WASI_NS_010
  Scenario: fd_readdir continues from its cookies over a union directory
    Given a process with a union directory holding repeated names open as descriptor 3
    When the application calls fd_readdir with small buffers, passing back each cookie it got
    Then it receives "." and ".." first, as the Preview 1 WITX says, and then every entry in union order, repeated names included, as WASI dirents
    And ".." reports nothing about the directory above "/data"
    And a record cut short by the buffer is completed by the next call, as Preview 1 says
    And a cookie of zero starts again from the first entry
    And a cookie it was never given fails without disturbing the directory
    And no raw 9P stat record reaches the application

  @WASI_NS_011
  Scenario: A kernel call the application waits on holds only the application's thread
    Given an application blocked in fd_read on an empty pipe
    Then other processes, the application's parent among them, keep running
    And when the pipe's other end writes "x", the fd_read returns "x"

  @WASI_NS_012 @security
  Scenario: An application looping without host calls is still contained
    Given an application looping forever without calling the host
    When its deadline under Isolation.md expires
    Then its execution is stopped without relying on a token or disposing its instance
    And the rest of the kernel stays responsive

  @WASI_NS_013
  Scenario Outline: Operations the namespace cannot honour are explicit
    Given a process with "/data" open as descriptor 3
    When the application calls <call>
    Then it fails with NOTSUP and no copy, delete or emulation takes place

    Examples:
      | call                                                 |
      | path_rename from "/data/a" to "/data/sub/a"          |
      | path_symlink                                         |
      | path_link                                            |
      | fd_sync on a provider without a durability guarantee |

  @WASI_NS_014
  Scenario: A trap ends the process without rolling back what it wrote
    Given a process that forks a child holding "/tmp/scratch" open ORCLOSE
    When the child execs an application that writes "partial" to "/data/out" and then traps
    Then the parent's wait returns a Waitmsg whose status starts "wasm-trap: "
    And "/data/out" holds "partial"
    And "/tmp/scratch" has been removed, as when any process exits

  @WASI_NS_022
  Scenario Outline: Kernel errors reach the application as Preview 1 errnos
    Given a process with "/data" open as descriptor 3
    When the application calls <call> and the kernel fails with "<error>"
    Then the call returns <errno>

    Examples:
      | call                                   | error                              | errno    |
      | path_open on "missing"                 | file does not exist                | NOENT    |
      | path_open on "report" with CREATE EXCL | file already exists                | EXIST    |
      | path_open on "locked" for writing      | permission denied                  | ACCES    |
      | path_unlink_file on "sub"              | file is a directory                | ISDIR    |
      | path_remove_directory on "report"      | not a directory                    | NOTDIR   |
      | path_remove_directory on "full"        | directory is not empty             | NOTEMPTY |
      | fd_write on a file in "/data"          | file system full                   | NOSPC    |
      | path_create_directory on "union/new"   | mounted directory forbids creation | ROFS     |
      | fd_read on a file in "/data"           | i/o timeout                        | IO       |

  @WASI_NS_023
  Scenario: poll_oneoff waits on descriptors and clocks, holding only the application's thread
    Given an application with an empty pipe as descriptor 0
    When it calls poll_oneoff with an fd_read subscription on descriptor 0 and a relative clock subscription of one second
    Then the clock subscription's event is returned after one second, and the read subscription's is not
    When it calls poll_oneoff again with the same subscriptions, and the pipe's other end writes "x"
    Then the read subscription's event is returned, with one byte ready
    And poll_oneoff with no subscriptions fails with INVAL

  @WASI_NS_024
  Scenario Outline: Clocks and randomness come from the host and grant nothing else
    Given an application
    When it calls <call>
    Then <result>

    Examples:
      | call                                         | result                                                    |
      | clock_time_get on the monotonic clock twice  | the second time is not before the first                   |
      | clock_time_get on the realtime clock         | it returns the host's time in nanoseconds since the epoch |
      | clock_time_get on the process CPU-time clock | it fails with NOTSUP                                      |
      | clock_time_get on the thread CPU-time clock  | it fails with NOTSUP                                      |
      | random_get for 32 bytes twice                | the two results differ                                    |
      | sched_yield                                  | it returns success                                        |
      | proc_raise                                   | it fails with NOSYS                                       |

  @WASI_NS_025
  Scenario Outline: No descriptor is a socket
    Given a process with a pipe open as descriptor 3
    When the application calls <call> on descriptor 3
    Then it fails with NOTSOCK

    Examples:
      | call          |
      | sock_accept   |
      | sock_recv     |
      | sock_send     |
      | sock_shutdown |

  @WASI_NS_026
  Scenario: fd_filestat_get converts the file's stat without inventing POSIX identity
    Given a process with "/data/report" open as descriptor 3, last modified at 2026-10-01 12:00 UTC
    When the application calls fd_filestat_get on descriptor 3
    Then the filestat's type is a regular file, its size is the file's length and its inode is the file's qid path
    And its modification time, and its status-change time, are 2026-10-01 12:00 UTC in nanoseconds
    And its link count is 1 and its device is 0

  @WASI_NS_027
  Scenario: fd_sync on gefs commits what the application wrote
    Given a process with a file on gefs open for writing as descriptor 3
    When the application writes "kept" and calls fd_sync on descriptor 3
    Then gefs has committed, and "kept" is in the file after the store is opened again

  @WASI_NS_028
  Scenario: The Preview 1 tests of wasi-testsuite pass
    Given wasi-testsuite at 5ea11d8, and Fog's list of expected failures with the reason for each
    When its Preview 1 tests run as Fog applications
    Then every test passes except those listed, and each listed one still fails as its reason says
