@fog_coyote
Feature: Notes reach a process once in every interleaving Coyote explores
  A note posted to a process interrupts a kernel call it is blocked in, as postnote does, and is
  delivered as the call returns. Here processes read pipes, the queues of which Coyote controls, and
  notes are posted by writing /proc/n/note while other tasks write to the pipe or the process ends.
  What must hold is that a read gives what was written or fails "interrupted" without losing it, that
  each note posted is seen once and no more, and that no call is left waiting. Coyote fails a
  schedule in which a task never ends. Scenarios are skipped unless the assemblies were rewritten
  with coyote rewrite.

  Background:
    Given a kernel whose child, which continues after every note, reads a pipe

  @FOG_COYOTE_401
  Scenario: A note racing a write to the pipe the child reads loses nothing
    When the parent writes "data" to the pipe while another task posts "hangup" to the child
    Then in every explored schedule the child reads "data", at once or after its read fails "interrupted"
    And in every explored schedule the child's handler saw "hangup" once

  @FOG_COYOTE_402
  Scenario: Two notes posted at once to a child blocked reading are each seen once
    When 2 tasks each post a note to the child at once, and then the parent writes "data" to the pipe
    Then in every explored schedule the child's handler saw each note once
    And in every explored schedule the child reads "data", at once or after its read fails "interrupted"

  @FOG_COYOTE_403
  Scenario: A note racing the child's end is posted or refused, and the child ends either way
    When the parent closes the pipe while another task posts "hangup" to the child
    Then in every explored schedule the note is posted or refused as the child has exited, and the child ends
