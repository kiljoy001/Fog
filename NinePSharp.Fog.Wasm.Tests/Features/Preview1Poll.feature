@fog_wasm_preview1_poll
Feature: poll_oneoff waits on descriptors and clocks, holding only the application's thread
  Plan 9 has no select, so readiness is looked at every 10 ms, a clock's resolution here, until a
  subscription is ready. A clock is ready once its deadline has passed, relative to the call or, with the absolute flag, a time on
  that clock; the deadline is counted down by the time waited. fd_read on a pipe is ready once bytes
  are queued for that end, which a Plan 9 pipe's stat length gives, or the other end has gone, which
  the event's hangup flag says; on any other descriptor it is ready at once, with what is left past
  its offset. fd_write is ready at once. A subscription that cannot be looked at, a descriptor not
  open or without the poll right, or a clock Fog does not keep, is ready with its errno. Every ready
  subscription is an event; with none, poll_oneoff is INVAL. The trace interpreter gives each
  subscription its index as userdata and traces each event as (userdata errno type nbytes flags).

  Background:
    Given a kernel that runs WASM applications

  @WASI_NS_023
  Scenario: A pipe is ready once bytes are queued, a clock once its time has passed, and hangup is flagged
    Given once the interpreter is ready, the parent writes to descriptor 6 and closes its end
    When the trace interpreter runs:
      """
      poll_oneoff read 6 clock 1 50000000 0
      fd_write 7 "ready"
      poll_oneoff read 6 clock 1 10000000000 0
      fd_read 6 1
      poll_oneoff read 6
      fd_read 6 1
      poll_oneoff read 6
      poll_oneoff
      """
    Then the trace is:
      """
      poll_oneoff read 6 clock 1 50000000 0 -> ok (1 SUCCESS 0 0 0)
      fd_write 7 "ready" -> ok 5
      poll_oneoff read 6 clock 1 10000000000 0 -> ok (0 SUCCESS 1 2 *)
      fd_read 6 1 -> ok "g"
      poll_oneoff read 6 -> ok (0 SUCCESS 1 1 1)
      fd_read 6 1 -> ok "o"
      poll_oneoff read 6 -> ok (0 SUCCESS 1 0 1)
      poll_oneoff -> INVAL
      """

  @WASI_NS_023
  Scenario: A file is ready at once with what is left past its offset, and writing is ready at once
    Given the file "/tmp/f" holds "0123456789"
    And the interpreter has "/tmp/f" open for reading and writing as descriptor 3
    And the interpreter has "/tmp" open for reading as descriptor 4
    When the trace interpreter runs:
      """
      fd_seek 3 4 set
      poll_oneoff read 3 write 3 read 4
      fd_seek 3 20 set
      poll_oneoff read 3
      """
    Then the trace is:
      """
      fd_seek 3 4 set -> ok 4
      poll_oneoff read 3 write 3 read 4 -> ok (0 SUCCESS 1 6 0) (1 SUCCESS 2 0 0) (2 SUCCESS 1 0 0)
      fd_seek 3 20 set -> ok 20
      poll_oneoff read 3 -> ok (0 SUCCESS 1 0 0)
      """

  @WASI_NS_023
  Scenario: Clocks fire relative to the call or at a time on that clock
    When the trace interpreter runs:
      """
      poll_oneoff clock 1 0 0
      poll_oneoff clock 0 1790856000000000000 1
      poll_oneoff clock 0 1790856000020000000 1
      poll_oneoff clock 1 5000000000 1 clock 1 30000000 0
      poll_oneoff clock 1 0 0 clock 1 5000000 0
      """
    Then the trace is:
      """
      poll_oneoff clock 1 0 0 -> ok (0 SUCCESS 0 0 0)
      poll_oneoff clock 0 1790856000000000000 1 -> ok (0 SUCCESS 0 0 0)
      poll_oneoff clock 0 1790856000020000000 1 -> ok (0 SUCCESS 0 0 0)
      poll_oneoff clock 1 5000000000 1 clock 1 30000000 0 -> ok (0 SUCCESS 0 0 0)
      poll_oneoff clock 1 0 0 clock 1 5000000 0 -> ok (0 SUCCESS 0 0 0)
      """

  @WASI_NS_023
  Scenario: Waiting for a clock takes at least its time
    When the trace interpreter runs:
      """
      poll_oneoff clock 1 300000000 0
      """
    Then the trace is:
      """
      poll_oneoff clock 1 300000000 0 -> ok (0 SUCCESS 0 0 0)
      """
    And the run took at least 300 ms

  @WASI_NS_023
  Scenario: A subscription that cannot be looked at is ready with its errno
    Given the file "/tmp/f" holds "x"
    And the interpreter has "/tmp/f" open for reading as descriptor 3
    When the trace interpreter runs:
      """
      poll_oneoff read 9 write 9
      poll_oneoff clock 2 0 0 clock 4 0 0
      fd_fdstat_set_rights 3 0x2 0
      poll_oneoff read 3 write 3
      """
    Then the trace is:
      """
      poll_oneoff read 9 write 9 -> ok (0 BADF 1 0 0) (1 BADF 2 0 0)
      poll_oneoff clock 2 0 0 clock 4 0 0 -> ok (0 NOTSUP 0 0 0) (1 INVAL 0 0 0)
      fd_fdstat_set_rights 3 0x2 0 -> ok
      poll_oneoff read 3 write 3 -> ok (0 NOTCAPABLE 1 0 0) (1 NOTCAPABLE 2 0 0)
      """
