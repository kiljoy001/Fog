@fog_draining
Feature: Draining in-flight 9P work is bounded and leaves unfinished work with an unknown outcome
  Closing a session, Tversion and Tflush cancel in-flight requests and wait at most the drain limit
  for them. A request still running after that is abandoned: it is answered with Rerror "unknown",
  its outcome is logged as unknown, and nothing waits for it again. Its effect may still happen,
  as flush(5) says of a request flushed before its reply. A connection whose requests do not finish
  within the drain limit after it ends still releases its socket and its slot, and disposing a
  listener closes connections that have not finished within the drain limit.

  Background:
    Given a control export with a drain limit of 200 milliseconds
    And a node session with "file" open for writing

  @FOG_DRAIN_001
  Scenario Outline: A write that ignores cancellation is abandoned with an unknown outcome
    Given a write to "file" that ignores cancellation is in flight
    When <drain>
    Then it finishes within 2 seconds
    And the write is answered with Rerror "unknown"
    And the write's outcome is logged as unknown

    Examples:
      | drain                       |
      | the session closes          |
      | the node sends Tversion     |
      | the node flushes the write  |

  @FOG_DRAIN_001
  Scenario: A flush of an abandoned write is answered with Rflush
    Given a write to "file" that ignores cancellation is in flight
    When the node flushes the write
    Then the flush is answered with Rflush

  @FOG_DRAIN_002
  Scenario: A write that honours cancellation is interrupted, not abandoned
    Given a write to "file" that honours cancellation is in flight
    When the session closes
    Then the write is answered with Rerror "interrupted"
    And no outcome is logged as unknown

  @FOG_DRAIN_003
  Scenario: An abandoned write still running keeps its fid busy
    Given a write to "file" that ignores cancellation is in flight
    When the node flushes the write
    And the node writes to "file" again
    Then that write is answered with Rerror "busy"

  @FOG_DRAIN_003
  Scenario: A flushed tag can be used again while its abandoned write still runs
    Given a write to "file" that ignores cancellation is in flight
    When the node flushes the write
    And the node stats the root with the write's tag
    Then that request is answered with Rstat

  @FOG_DRAIN_004
  Scenario: A connection whose request never finishes still releases its socket and slot
    Given a user listener admitting 1 connection with a drain limit of 200 milliseconds over a dispatcher that never answers
    And a client that sent a request and closed its connection
    Then a second client is served within 2 seconds
    And the abandoned request is logged as unknown

  @FOG_DRAIN_005
  Scenario: Disposing a listener closes a connection that has not finished
    Given a user listener admitting 1 connection with a drain limit of 200 milliseconds over a dispatcher that never answers
    And a client that sent a request and kept its connection open
    When the listener is disposed
    Then disposal finishes within 2 seconds
    And the client's connection is closed
    And no connection had to be force-closed
