@fog_flushing
Feature: Every Tflush is answered with Rflush, as flush(5) requires
  flush(5): a Tflush can never be responded to by an Rerror message. The server answers Rflush once
  the flushed request is answered or abandoned, and at once when there is nothing to wait for. Of
  several flushes of one request only the last needs an answer, because an Rflush implies an
  answer for all previous ones; so each pending request has at most one waiting flush, and a newer
  flush finishes the older one without a reply. Flushes are never refused for want of a slot.
  Protocol violations that make a message unanswerable, such as NOTAG or a tag already in use, are
  still rejected.

  Background:
    Given a control export with a drain limit of 5 seconds
    And a node session with "file" open for writing

  @FOG_FLUSH_001
  Scenario Outline: A flush with nothing to wait for is answered with Rflush at once
    Given a write to "file" that finishes when it is released
    When the node flushes <target>
    Then that flush is answered with Rflush while the write still runs

    Examples:
      | target         |
      | an unused tag  |
      | its own tag    |
      | the write's flush |

  @FOG_FLUSH_002
  Scenario: A newer flush of a request finishes the older one without a reply
    Given a write to "file" that finishes when it is released
    When the node flushes the write
    And the node flushes the write again
    Then the first flush finishes without a reply while the write still runs
    And once the write is released the second flush is answered with Rflush

  @FOG_FLUSH_003
  Scenario: A session using every request slot can still flush each request
    Given the session admits 2 requests
    And 2 writes to "file" that finish when they are released
    When the node flushes both writes
    Then neither flush is refused while the writes still run
    And once the writes are released both flushes are answered with Rflush

  @FOG_FLUSH_004
  Scenario Outline: A flush whose request is aborted by a reset is answered with Rflush
    Given a write to "file" that finishes when it is released or cancelled
    When the node flushes the write
    And <reset>
    Then the flush is answered with Rflush

    Examples:
      | reset                   |
      | the node sends Tversion |
      | the session closes      |
