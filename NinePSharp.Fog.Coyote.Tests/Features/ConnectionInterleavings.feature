@fog_coyote
Feature: A node's connection holds in every interleaving Coyote explores
  The node listener hands each connection to NinePSharp's connection processor, which reads frames,
  runs each request as a task of its own, and writes the replies under one lock. Here the processor
  and the dispatcher behind it serve a client over an in-memory stream that Coyote controls, with
  the session given the node's certificate as the listener gives it after TLS. Coyote does not model
  time, so the processor's drain limit can pass at any step, and its warnings that an outcome is
  unknown say nothing here; what must hold is that the session answers or interrupts every request
  and abandons none. Every schedule ends with the client hanging up, and fails if the connection
  then does not end. Scenarios are skipped unless the assemblies were rewritten with coyote rewrite.

  Background:
    Given a node connection with "file" open for writing, whose session's drain limit is 5 minutes

  @FOG_COYOTE_017
  Scenario: A client that sends a tag again as soon as its reply arrives is served
    Given the node's writes to "file" finish when they are released
    When the client writes with tag 100, another task releases the write, and the client stats with tag 100 the moment Rwrite arrives
    Then in every explored schedule the connection answers the write with Rwrite
    And in every explored schedule the connection answers the stat with Rstat

  @FOG_COYOTE_018
  Scenario: A flush racing its write's release is answered after the write's reply, if any
    Given the node's writes to "file" finish when they are released
    When the client writes with tag 100 and flushes it while another task releases the write
    Then in every explored schedule the connection answers the write, if at all, before Rflush
    And in every explored schedule the connection answers a stat with tag 100 after Rflush with Rstat

  @FOG_COYOTE_019
  Scenario: Tversion with a write in flight is answered after the write
    Given the node's writes to "file" finish when they are released or cancelled
    When the client writes with tag 100 and sends Tversion while another task releases the write
    Then in every explored schedule the connection answers the write with Rwrite or Rerror "interrupted" before Rversion
    And in every explored schedule the client attaches again after Rversion

  @FOG_COYOTE_020
  Scenario: A client hanging up with a write in flight ends the connection and interrupts the write
    Given the node's writes to "file" finish when they are released or cancelled
    When the client writes with tag 100 and hangs up while another task releases the write
    Then in every explored schedule the connection answers the write with Rwrite or Rerror "interrupted"
    And in every explored schedule the session abandons no request

  @FOG_COYOTE_021
  Scenario: A connection whose lifetime ends with a write in flight ends
    Given the node's writes to "file" finish when they are released or cancelled
    When the client writes with tag 100 and the connection's lifetime ends while another task releases the write
    Then in every explored schedule the session abandons no request
