@fog_wasm_preview1_descriptors
Feature: Preview 1 calls on the descriptors an application holds
  The descriptors are the process's own, with the same numbers. fd_read and fd_write read and write
  at the descriptor's offset and move it; fd_pread and fd_pwrite act at the offset given and leave
  the descriptor's alone; a pipe has no offset, so seeking one is SPIPE. A vector of buffers is filled or emptied in order, from one kernel call.
  WASI offsets are unsigned, so one above the largest signed offset, which would reach Plan 9's -1,
  is INVAL. fd_seek moves the offset from the start, from where it is or from the end, never before
  the start. A directory has no right to be read, written or seeked, so those are NOTCAPABLE, and
  its 9P directory entries are never given to the application. fd_close closes a descriptor and
  fd_renumber is the process's renumber: it moves a descriptor to another number in one step,
  closing what that number named. The scenarios run the trace interpreter, which makes each call
  of its script and traces the result.

  Background:
    Given a kernel that runs WASM applications

  @WASI_NS_015
  Scenario: fd_read reads at the descriptor's offset, filling the buffers in order
    Given the file "/tmp/f" holds "hello world"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    When the trace interpreter runs:
      """
      fd_read 3 5 3
      fd_read 3 2 100
      fd_read 3 10
      """
    Then the trace is:
      """
      fd_read 3 5 3 -> ok "hello wo"
      fd_read 3 2 100 -> ok "rld"
      fd_read 3 10 -> ok ""
      """

  @WASI_NS_015
  Scenario: fd_write writes at the descriptor's offset, emptying the buffers in order
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading and writing as descriptor 3
    When the trace interpreter runs:
      """
      fd_seek 3 2 set
      fd_write 3 "ab" "cd"
      fd_tell 3
      """
    Then the trace is:
      """
      fd_seek 3 2 set -> ok 2
      fd_write 3 "ab" "cd" -> ok 4
      fd_tell 3 -> ok 6
      """
    And the file "/tmp/f" holds "01abcd6789"

  @WASI_NS_009
  Scenario: Positioned IO acts at its offset and leaves the descriptor's alone
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading and writing as descriptor 3
    When the trace interpreter runs:
      """
      fd_read 3 7
      fd_pread 3 0 3
      fd_pwrite 3 1 "ab"
      fd_tell 3
      fd_read 3 10
      """
    Then the trace is:
      """
      fd_read 3 7 -> ok "0123456"
      fd_pread 3 0 3 -> ok "012"
      fd_pwrite 3 1 "ab" -> ok 2
      fd_tell 3 -> ok 7
      fd_read 3 10 -> ok "789"
      """
    And the file "/tmp/f" holds "0ab3456789"

  @WASI_NS_009
  Scenario: Positioned IO cannot reach Plan 9's implicit-offset sentinel
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading and writing as descriptor 3
    When the trace interpreter runs:
      """
      fd_seek 3 7 set
      fd_pread 3 18446744073709551615 4
      fd_pwrite 3 9223372036854775808 "x"
      fd_tell 3
      """
    Then the trace is:
      """
      fd_seek 3 7 set -> ok 7
      fd_pread 3 18446744073709551615 4 -> INVAL
      fd_pwrite 3 9223372036854775808 "x" -> INVAL
      fd_tell 3 -> ok 7
      """
    And the file "/tmp/f" holds "0123456789"

  @WASI_NS_009
  Scenario: fd_seek moves from the start, from where it is or from the end, never before the start
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    When the trace interpreter runs:
      """
      fd_seek 3 4 set
      fd_seek 3 2 cur
      fd_seek 3 -1 end
      fd_seek 3 -20 cur
      fd_seek 3 0 3
      fd_tell 3
      """
    Then the trace is:
      """
      fd_seek 3 4 set -> ok 4
      fd_seek 3 2 cur -> ok 6
      fd_seek 3 -1 end -> ok 9
      fd_seek 3 -20 cur -> INVAL
      fd_seek 3 0 3 -> INVAL
      fd_tell 3 -> ok 9
      """

  @WASI_NS_009
  Scenario: A directory has no right to be read, written or seeked
    Given the interpreter has "/tmp" open for reading as descriptor 3
    When the trace interpreter runs:
      """
      fd_seek 3 0 set
      fd_tell 3
      fd_read 3 4096
      fd_pread 3 0 4096
      fd_write 3 "x"
      fd_filestat_set_size 3 0
      """
    Then the trace is:
      """
      fd_seek 3 0 set -> NOTCAPABLE
      fd_tell 3 -> NOTCAPABLE
      fd_read 3 4096 -> NOTCAPABLE
      fd_pread 3 0 4096 -> NOTCAPABLE
      fd_write 3 "x" -> NOTCAPABLE
      fd_filestat_set_size 3 0 -> NOTCAPABLE
      """

  @WASI_NS_006
  Scenario: fd_renumber moves a descriptor in one step, closing what the number named
    Given the file "/tmp/f" holds "0123456789"
    And the file "/tmp/g" holds "abcdefghij"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    And the interpreter has "/tmp/g" open for reading as descriptor 4
    When the trace interpreter runs:
      """
      fd_seek 3 7 set
      fd_renumber 3 4
      fd_read 4 3
      fd_read 3 3
      fd_renumber 4 4
      fd_read 4 3
      fd_renumber 9 4
      """
    Then the trace is:
      """
      fd_seek 3 7 set -> ok 7
      fd_renumber 3 4 -> ok
      fd_read 4 3 -> ok "789"
      fd_read 3 3 -> BADF
      fd_renumber 4 4 -> ok
      fd_read 4 3 -> ok ""
      fd_renumber 9 4 -> BADF
      """

  @WASI_NS_015
  Scenario: fd_close closes a descriptor once
    Given the file "/tmp/f" holds "x"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    When the trace interpreter runs:
      """
      fd_close 3
      fd_close 3
      fd_read 3 1
      """
    Then the trace is:
      """
      fd_close 3 -> ok
      fd_close 3 -> BADF
      fd_read 3 1 -> BADF
      """

  @WASI_NS_009
  Scenario: A pipe has no offset, so seeking it is SPIPE
    When the trace interpreter runs:
      """
      fd_seek 1 0 set
      fd_tell 1
      """
    Then the trace is:
      """
      fd_seek 1 0 set -> SPIPE
      fd_tell 1 -> SPIPE
      """
