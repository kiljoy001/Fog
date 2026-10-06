@fog_coyote
Feature: Draining holds in every interleaving Coyote explores
  Coyote controls the tasks, waits and continuations in the rewritten Fog assemblies and explores
  the orders they can run in. Each scenario repeats one race many times and fails on the first
  schedule that breaks the rule, printing a trace that replays it. Waits Coyote does not control
  are listed in the scenario's output, because the orders around them are explored less.
  Scenarios are skipped unless the assemblies were rewritten with coyote rewrite.

  Background:
    Given a node session with "file" open for writing on a dispatcher whose drain limit is 5 minutes

  @FOG_COYOTE_001
  Scenario: A flushed tag can be used again as soon as the flush is answered
    Given a write to "file" that finishes when it is released
    When the node flushes the write while another task releases it
    Then in every explored schedule the write's tag can be used again the moment the flush is answered

  @FOG_COYOTE_002
  Scenario: A write that honours cancellation is interrupted or finishes, never abandoned
    Given a write to "file" that finishes when it is released or cancelled
    When the session closes while another task releases the write
    Then in every explored schedule the write is answered with Rwrite or Rerror "interrupted"
    And no explored schedule logs an outcome as unknown

  @FOG_COYOTE_003
  Scenario: A version reset frees the write's tag by the time Rversion is answered
    Given a write to "file" that finishes when it is released or cancelled
    When the node sends Tversion while another task releases the write
    Then in every explored schedule the write is answered with Rwrite or Rerror "interrupted"
    And in every explored schedule the write's tag can be used again the moment Rversion is answered

  @FOG_COYOTE_004
  Scenario: Of two flushes of one write only the last needs an answer, and both leave its tag free
    Given a write to "file" that finishes when it is released
    When the node flushes the write twice while another task releases it
    Then in every explored schedule each flush is answered with Rflush or finishes without a reply, and one with Rflush
    And in every explored schedule the write's tag can be used again the moment the flushes are answered

  @FOG_COYOTE_005
  Scenario: A flush racing the session close is answered Rflush unless the session was already gone
    Given a write to "file" that finishes when it is released or cancelled
    When the node flushes the write while the session closes
    Then in every explored schedule the write is answered with Rwrite or Rerror "interrupted"
    And in every explored schedule the flush is answered with Rflush or Rerror "not-ready"
    And no explored schedule logs an outcome as unknown

  @FOG_COYOTE_006
  Scenario: A write that never finishes is abandoned once, and its flushed tag is free
    Given the dispatcher's drain limit is 50 milliseconds
    And a write to "file" that never finishes
    When the node flushes the write
    Then in every explored schedule the write is answered with Rerror "unknown"
    And in every explored schedule the flush is answered with Rflush
    And in every explored schedule the write's tag can be used again the moment the flushes are answered
    And in every explored schedule the write's outcome is logged as unknown exactly once
