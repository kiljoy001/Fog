@fog_wasm_preview1_host
Feature: Preview 1's clocks, randomness and sockets come from the host and grant nothing else
  The realtime clock is the host's wall time in nanoseconds since the epoch, counted in 100 ns
  ticks; the monotonic clock is the host's, in its own resolution. Fog keeps no process or thread
  CPU-time clock, so those are NOTSUP, and a clock Preview 1 does not name is INVAL. random_get
  fills its buffer from the host's cryptographic source, fresh on every call. sched_yield
  succeeds: the application has a thread of its own. No descriptor is a socket, so the socket
  calls are NOTSOCK on any open descriptor. The kernel's clock stands at 2026-10-01 12:00 UTC and
  its monotonic clock, which counts microseconds, at five seconds.

  Background:
    Given a kernel that runs WASM applications

  @WASI_NS_024
  Scenario: The clocks are the host's
    When the trace interpreter runs:
      """
      clock_time_get 0 0
      clock_time_get 1 0
      clock_res_get 0
      clock_res_get 1
      clock_time_get 2 0
      clock_time_get 3 0
      clock_res_get 2
      clock_time_get 4 0
      clock_res_get 4
      """
    Then the trace is:
      """
      clock_time_get 0 0 -> ok 1790856000000000000
      clock_time_get 1 0 -> ok 5000000000
      clock_res_get 0 -> ok 100
      clock_res_get 1 -> ok 1000
      clock_time_get 2 0 -> NOTSUP
      clock_time_get 3 0 -> NOTSUP
      clock_res_get 2 -> NOTSUP
      clock_time_get 4 0 -> INVAL
      clock_res_get 4 -> INVAL
      """

  @WASI_NS_024
  Scenario: random_get is fresh on every call
    When the trace interpreter runs:
      """
      random_get 32
      random_get 32
      random_get 0
      """
    Then the trace has 3 lines, the first two of 32 random bytes that differ
    And its last line is "random_get 0 -> ok """

  @WASI_NS_024
  Scenario: sched_yield succeeds and proc_raise is NOSYS
    When the trace interpreter runs:
      """
      sched_yield
      proc_raise 15
      """
    Then the trace is:
      """
      sched_yield -> ok
      proc_raise 15 -> NOSYS
      """

  @WASI_NS_025
  Scenario: No descriptor is a socket
    Given the file "/tmp/f" holds "x"
    And the interpreter has "/tmp/f" open for reading and writing as descriptor 3
    When the trace interpreter runs:
      """
      sock_accept 3 0
      sock_recv 3 0 8
      sock_send 3 0 "x"
      sock_shutdown 3 2
      sock_shutdown 9 2
      """
    Then the trace is:
      """
      sock_accept 3 0 -> NOTSOCK
      sock_recv 3 0 8 -> NOTSOCK
      sock_send 3 0 "x" -> NOTSOCK
      sock_shutdown 3 2 -> NOTSOCK
      sock_shutdown 9 2 -> BADF
      """
