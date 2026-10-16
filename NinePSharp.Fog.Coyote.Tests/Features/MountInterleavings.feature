@fog_coyote
Feature: A mount's connection hands every reply to its caller in every interleaving Coyote explores
  The mount driver shares one connection to a file server among every call on the mount, as devmnt
  does. Each request has a tag of its own; one caller at a time reads replies and hands each to the
  request it answers, and when its own reply has come it stands down and wakes the next caller
  still waiting, which then reads. A caller cancelled while it waits flushes its request and waits
  for the flush's reply; the request's own reply, if it came first, is still the call's. When the
  server hangs up, every caller still waiting fails, and so does every later call. Here the
  connection runs over an in-memory transport that Coyote controls, to a server that answers each
  request when the scenario lets it, in any order. Coyote fails a schedule in which a call never
  ends. Scenarios are skipped unless the assemblies were rewritten with coyote rewrite.

  Background:
    Given a mount connection to a server that holds its replies

  @FOG_COYOTE_301
  Scenario: Callers whose replies come in any order each get their own
    When 3 callers each stat a different file while another task answers them in reverse order
    Then in every explored schedule each caller gets the stat of its own file
    And in every explored schedule no tag is used by two requests at once

  @FOG_COYOTE_302
  Scenario: A caller cancelled while its reply races the cancellation flushes it, and the mount goes on
    When 2 callers each stat a file, one of them cancelled while another task answers both
    Then in every explored schedule the cancelled caller gets its stat or is cancelled, and is cancelled only after the server has its flush
    And in every explored schedule the other caller gets the stat of its own file
    And in every explored schedule a later stat on the connection gets its reply

  @FOG_COYOTE_303
  Scenario: A server hanging up with callers waiting fails each of them, and every later call
    When 2 callers each stat a file while another task answers one of them and the server hangs up
    Then in every explored schedule each caller gets the stat of its own file or fails with "mount rpc error"
    And in every explored schedule a later stat on the connection fails with "mount rpc error"

  @FOG_COYOTE_304
  Scenario: A server's error is the call's, and the connection goes on
    When 2 callers each stat a file while another task refuses one with "file does not exist" and answers the other
    Then in every explored schedule the refused caller fails with "file does not exist"
    And in every explored schedule the other caller gets the stat of its own file
