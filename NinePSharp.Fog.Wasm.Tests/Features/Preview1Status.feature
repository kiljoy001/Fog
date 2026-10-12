@fog_wasm_preview1_status
Feature: Preview 1's descriptor status, rights and file status
  fd_fdstat_get reports a descriptor's file type from its qid, a directory or a regular file, and
  its rights. Each descriptor starts with every right: Plan 9 opens a directory only for reading,
  so the open mode cannot carry what the application may do beneath it, and the file server still
  checks every call. fd_fdstat_set_rights only narrows; nothing restores a right, and a call needs
  every right it names or it is NOTCAPABLE, before anything else is done. fd_renumber moves a
  descriptor's rights with it. Fog keeps no append, sync or nonblocking flags, so asking for one is
  NOTSUP. fd_filestat_get converts the file's stat without inventing POSIX identity: device 0, the
  qid path as inode, one link, and the modification time as the status-change time, in nanoseconds.
  The length and the modification time can be set, the time in Plan 9's whole seconds; the access
  time cannot. Advice is accepted and ignored, and space cannot be allocated. The kernel's clock
  stands at 2026-10-01 12:00 UTC, 1790856000 seconds.

  Background:
    Given a kernel that runs WASM applications

  @WASI_NS_004
  Scenario: fd_fdstat_get reports the file type and every right
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    And the interpreter has "/tmp" open for reading as descriptor 4
    When the trace interpreter runs:
      """
      fd_fdstat_get 3
      fd_fdstat_get 4
      fd_fdstat_get 9
      """
    Then the trace is:
      """
      fd_fdstat_get 3 -> ok filetype=4 flags=0 base=0x3fffffff inheriting=0x3fffffff
      fd_fdstat_get 4 -> ok filetype=3 flags=0 base=0x3fffffff inheriting=0x3fffffff
      fd_fdstat_get 9 -> BADF
      """

  @WASI_NS_004
  Scenario: Rights only narrow
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    When the trace interpreter runs:
      """
      fd_fdstat_set_rights 3 0x6 0x2
      fd_fdstat_get 3
      fd_read 3 2
      fd_fdstat_set_rights 3 0x46 0x2
      fd_fdstat_set_rights 3 0x6 0x6
      fd_fdstat_set_rights 3 0x2 0
      fd_fdstat_get 3
      fd_fdstat_set_rights 9 0 0
      """
    Then the trace is:
      """
      fd_fdstat_set_rights 3 0x6 0x2 -> ok
      fd_fdstat_get 3 -> ok filetype=4 flags=0 base=0x6 inheriting=0x2
      fd_read 3 2 -> ok "01"
      fd_fdstat_set_rights 3 0x46 0x2 -> NOTCAPABLE
      fd_fdstat_set_rights 3 0x6 0x6 -> NOTCAPABLE
      fd_fdstat_set_rights 3 0x2 0 -> ok
      fd_fdstat_get 3 -> ok filetype=4 flags=0 base=0x2 inheriting=0x0
      fd_fdstat_set_rights 9 0 0 -> BADF
      """

  @WASI_NS_004
  Scenario: A call needs every right it names
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading and writing as descriptor 3
    When the trace interpreter runs:
      """
      fd_fdstat_set_rights 3 0x2 0
      fd_read 3 2
      fd_pread 3 0 2
      fd_fdstat_set_rights 3 0 0
      fd_read 3 2
      fd_pread 3 0 2
      fd_write 3 "x"
      fd_pwrite 3 0 "x"
      fd_seek 3 0 set
      fd_tell 3
      fd_fdstat_set_flags 3 0
      fd_filestat_get 3
      fd_filestat_set_size 3 0
      fd_filestat_set_times 3 0 0 0
      fd_advise 3 0 0 0
      fd_allocate 3 0 1
      fd_fdstat_get 3
      fd_close 3
      """
    Then the trace is:
      """
      fd_fdstat_set_rights 3 0x2 0 -> ok
      fd_read 3 2 -> ok "01"
      fd_pread 3 0 2 -> NOTCAPABLE
      fd_fdstat_set_rights 3 0 0 -> ok
      fd_read 3 2 -> NOTCAPABLE
      fd_pread 3 0 2 -> NOTCAPABLE
      fd_write 3 "x" -> NOTCAPABLE
      fd_pwrite 3 0 "x" -> NOTCAPABLE
      fd_seek 3 0 set -> NOTCAPABLE
      fd_tell 3 -> NOTCAPABLE
      fd_fdstat_set_flags 3 0 -> NOTCAPABLE
      fd_filestat_get 3 -> NOTCAPABLE
      fd_filestat_set_size 3 0 -> NOTCAPABLE
      fd_filestat_set_times 3 0 0 0 -> NOTCAPABLE
      fd_advise 3 0 0 0 -> NOTCAPABLE
      fd_allocate 3 0 1 -> NOTCAPABLE
      fd_fdstat_get 3 -> ok filetype=4 flags=0 base=0x0 inheriting=0x0
      fd_close 3 -> ok
      """
    And the file "/tmp/f" holds "0123456789"

  @WASI_NS_004
  Scenario: fd_renumber moves a descriptor's rights with it
    Given the file "/tmp/f" holds "f"
    And the file "/tmp/g" holds "g"
    And the file "/tmp/h" holds "h"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    And the interpreter has "/tmp/g" open for reading as descriptor 4
    And the interpreter has "/tmp/h" open for reading as descriptor 5
    When the trace interpreter runs:
      """
      fd_fdstat_set_rights 3 0x2 0
      fd_renumber 3 4
      fd_fdstat_get 4
      fd_renumber 5 4
      fd_fdstat_get 4
      """
    Then the trace is:
      """
      fd_fdstat_set_rights 3 0x2 0 -> ok
      fd_renumber 3 4 -> ok
      fd_fdstat_get 4 -> ok filetype=4 flags=0 base=0x2 inheriting=0x0
      fd_renumber 5 4 -> ok
      fd_fdstat_get 4 -> ok filetype=4 flags=0 base=0x3fffffff inheriting=0x3fffffff
      """

  @WASI_NS_013
  Scenario: Descriptor flags Fog does not keep are NOTSUP
    Given the file "/tmp/f" holds "x"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    When the trace interpreter runs:
      """
      fd_fdstat_set_flags 3 0
      fd_fdstat_set_flags 3 1
      fd_fdstat_set_flags 3 4
      fd_fdstat_set_flags 9 0
      """
    Then the trace is:
      """
      fd_fdstat_set_flags 3 0 -> ok
      fd_fdstat_set_flags 3 1 -> NOTSUP
      fd_fdstat_set_flags 3 4 -> NOTSUP
      fd_fdstat_set_flags 9 0 -> BADF
      """

  @WASI_NS_026
  Scenario: fd_filestat_get converts the file's stat without inventing POSIX identity
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    And the interpreter has "/tmp" open for reading as descriptor 4
    When the trace interpreter runs:
      """
      fd_filestat_get 3
      fd_filestat_get 4
      fd_filestat_get 9
      """
    Then the trace is:
      """
      fd_filestat_get 3 -> ok dev=0 ino=* filetype=4 nlink=1 size=10 atim=1790856000000000000 mtim=1790856000000000000 ctim=1790856000000000000
      fd_filestat_get 4 -> ok dev=0 ino=* filetype=3 nlink=1 size=0 atim=1790856000000000000 mtim=1790856000000000000 ctim=1790856000000000000
      fd_filestat_get 9 -> BADF
      """

  @WASI_NS_026
  Scenario: The inode is the file's qid path, so two files differ and one file is one inode
    Given the file "/tmp/f" holds "f"
    And the file "/tmp/g" holds "g"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    And the interpreter has "/tmp/g" open for reading as descriptor 4
    And the interpreter has "/tmp/f" open for reading as descriptor 5
    When the trace interpreter runs:
      """
      fd_filestat_get 3
      fd_filestat_get 4
      fd_filestat_get 5
      """
    Then descriptors 3 and 5 report one inode and descriptor 4 another

  @WASI_NS_026
  Scenario: fd_filestat_set_size sets the file's length
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading and writing as descriptor 3
    When the trace interpreter runs:
      """
      fd_filestat_set_size 3 4
      fd_filestat_get 3
      fd_filestat_set_size 9 4
      """
    Then the trace is:
      """
      fd_filestat_set_size 3 4 -> ok
      fd_filestat_get 3 -> ok dev=0 ino=* filetype=4 nlink=1 size=4 atim=* mtim=* ctim=*
      fd_filestat_set_size 9 4 -> BADF
      """
    And the file "/tmp/f" holds "0123"

  @WASI_NS_026
  Scenario: fd_filestat_set_times sets only the modification time, in whole seconds
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading and writing as descriptor 3
    When the trace interpreter runs:
      """
      fd_filestat_set_times 3 0 1700000000999999999 4
      fd_filestat_get 3
      fd_filestat_set_times 3 0 0 0
      fd_filestat_get 3
      fd_filestat_set_times 3 0 0 8
      fd_filestat_get 3
      fd_filestat_set_times 3 1700000000000000000 0 1
      fd_filestat_set_times 3 0 0 2
      fd_filestat_set_times 3 0 1700000000000000000 12
      fd_filestat_set_times 9 0 0 0
      """
    Then the trace is:
      """
      fd_filestat_set_times 3 0 1700000000999999999 4 -> ok
      fd_filestat_get 3 -> ok dev=0 ino=* filetype=4 nlink=1 size=10 atim=* mtim=1700000000000000000 ctim=1700000000000000000
      fd_filestat_set_times 3 0 0 0 -> ok
      fd_filestat_get 3 -> ok dev=0 ino=* filetype=4 nlink=1 size=10 atim=* mtim=1700000000000000000 ctim=1700000000000000000
      fd_filestat_set_times 3 0 0 8 -> ok
      fd_filestat_get 3 -> ok dev=0 ino=* filetype=4 nlink=1 size=10 atim=* mtim=1790856000000000000 ctim=1790856000000000000
      fd_filestat_set_times 3 1700000000000000000 0 1 -> NOTSUP
      fd_filestat_set_times 3 0 0 2 -> NOTSUP
      fd_filestat_set_times 3 0 1700000000000000000 12 -> INVAL
      fd_filestat_set_times 9 0 0 0 -> BADF
      """

  @WASI_NS_013
  Scenario: Advice is accepted and ignored, and space cannot be allocated
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading and writing as descriptor 3
    When the trace interpreter runs:
      """
      fd_advise 3 0 10 5
      fd_advise 3 0 10 6
      fd_advise 9 0 10 0
      fd_allocate 3 0 100
      fd_allocate 9 0 100
      """
    Then the trace is:
      """
      fd_advise 3 0 10 5 -> ok
      fd_advise 3 0 10 6 -> INVAL
      fd_advise 9 0 10 0 -> BADF
      fd_allocate 3 0 100 -> NOTSUP
      fd_allocate 9 0 100 -> BADF
      """
    And the file "/tmp/f" holds "0123456789"
