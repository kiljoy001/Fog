@fog_gefs
Feature: A change finishes before its blocks are written, and a commit waits only for the blocks queued before it, after 9front's gefs
  gefs's changes do not write their blocks: they queue them, and the sync process writes them, so
  a change goes on without waiting for the device, and a read finds a queued block before it is
  written. A commit waits for the blocks queued before it, then writes the arena headers and
  superblocks naming them. A block the device fails to write leaves the store read only, as gefs's
  sync process does, and what was queued can still be read.

  Fog's commit writes the arena headers and superblocks itself once its fence comes out, so it
  waits only for the blocks queued before it began. gefs queues them behind further fences, which
  also wait for blocks that changes queue meanwhile. A freed block goes back to its arena when its
  limbo ends rather than through the queue: it was queued before the commit that freed it, so it is
  written by then. Arena log blocks are written when they fill, as gefs writes them.

  Background:
    Given a device of 800 blocks reamed with 2 arenas by adm at time 100, served by a server
    And adm attaches to "main"
    And adm creates file notes in the root with mode 0666 at time 200
    And adm writes "kept" to notes at offset 0 at time 200
    And the server commits

  @FOG_GEFS_806
  Scenario: A change finishes before its blocks reach the device, and reads find them in the queue
    When the device holds its next write
    And adm writes "other" to notes at offset 0 at time 300
    Then reading 100 bytes of notes at offset 0 gives "other"
    When the device lets the write go
    And the server commits
    And the device is opened again, served by a server
    And adm attaches to "main"
    Then reading 100 bytes of notes at offset 0 gives "other"
    And every block of the device is held, in a log or free

  @FOG_GEFS_806
  Scenario: A commit waits for the blocks queued before it
    When the device holds its next write
    And adm writes "later" to notes at offset 0 at time 300
    And a commit starts
    Then the commit is still waiting
    When the device lets the write go
    Then the commit finishes
    When the device is opened again, served by a server
    And adm attaches to "main"
    Then reading 100 bytes of notes at offset 0 gives "later"
    And every block of the device is held, in a log or free

  @FOG_GEFS_806
  Scenario: A commit does not wait for the blocks queued after it began
    When adm writes "later" to notes at offset 0 at time 300
    And a commit starts, and stops at its first device flush
    And the device holds its next write
    And adm writes "again" to notes at offset 0 at time 300
    And the device lets the flush go
    Then the commit finishes
    When the device lets the write go
    And the server commits
    And the device is opened again, served by a server
    And adm attaches to "main"
    Then reading 100 bytes of notes at offset 0 gives "again"
    And every block of the device is held, in a log or free

  @FOG_GEFS_806
  Scenario: A block the device fails to write leaves the store read only, still reading what was queued
    When the device fails every write
    And adm writes "lost" to notes at offset 0 at time 300
    And 5 seconds pass
    Then the server's failure is "device write failed"
    And adm writing "x" to notes fails with "file system read only"
    And reading 100 bytes of notes at offset 0 gives "lost"
