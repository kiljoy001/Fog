@fog_gefs
Feature: Reads run beside the writer, and a commit writes to the device without holding it, after 9front's gefs
  gefs runs changes one at a time in its mutate process, under mutlk, while its read processes walk,
  read and stat without the lock. A tree's root is swapped under the tree's own small lock, so a
  read takes one root and sees the tree as it was then. A commit holds mutlk only to bring the
  snapshots up to date, write the arena logs and pack the headers and superblocks; it writes those
  and waits for the device with the lock released, so changes and reads carry on meanwhile, and a
  second commit waits for the first. A block a commit frees is not reused while a read that may
  still use it is running: it waits in limbo, as gefs's epochs keep it, until every read that began
  before it was freed has finished.

  Fog's writes take the writer's lock in the task that asks for them, rather than going through a
  mutate process on a channel; either way they run one at a time. Fog writes a block when it is
  made, rather than queueing it for a sync process. Time does not move while a commit is stopped,
  since the five-second commit would wait behind it.

  Background:
    Given a device of 800 blocks reamed with 2 arenas by adm at time 100, served by a server
    And adm attaches to "main"
    And adm creates file notes in the root with mode 0666 at time 200
    And adm writes "kept" to notes at offset 0 at time 200
    And the server commits

  @FOG_GEFS_801
  Scenario: Reads run while a change holds the writer
    When adm starts writing "other" to a new file other, and the change stops at its first device write
    Then reading 100 bytes of notes at offset 0 gives "kept"
    And the root lists notes
    And notes is a file "notes" owned by adm in group adm, mode 0664, version 1, modified at 200, 4 bytes long, last changed by adm
    When the device lets the write go
    Then the change finishes
    And reading 100 bytes of other at offset 0 gives "other"

  @FOG_GEFS_802
  Scenario: Changes and reads run while a commit waits for the device
    When adm writes "later" to notes at offset 0 at time 300
    And a commit starts, and stops at its first device flush
    And adm writes "again" to notes at offset 0 at time 300
    Then reading 100 bytes of notes at offset 0 gives "again"
    And the store is at generation 2
    When the device lets the flush go
    Then the commit finishes
    And the store is at generation 3
    When the server commits
    And the device is opened again, served by a server
    And adm attaches to "main"
    Then reading 100 bytes of notes at offset 0 gives "again"
    And every block of the device is held, in a log or free

  @FOG_GEFS_802
  Scenario: An open that truncates is a change, and waits for the change holding the writer
    When adm starts writing "other" to a new file other, and the change stops at its first device write
    And adm starts opening notes with truncation
    Then the open is still waiting
    When the device lets the write go
    Then the change finishes
    And the open finishes
    And reading 100 bytes of notes at offset 0 gives nothing

  @FOG_GEFS_802
  Scenario: A second commit waits for the first to finish
    When adm writes "later" to notes at offset 0 at time 300
    And a commit starts, and stops at its first device flush
    And a second commit starts
    Then the second commit is still waiting
    When the device lets the flush go
    Then the commit finishes
    And the second commit finishes
    And every block of the device is held, in a log or free

  @FOG_GEFS_803
  Scenario: Blocks a commit frees wait for the reads that began before they were freed
    When a read takes main's root
    And adm writes "fresh" to notes at offset 0 at time 300
    And the server commits
    And adm writes "newer" to notes at offset 0 at time 400
    And the server commits
    Then the blocks main's root used, and the change freed, are not free
    And the read that took main's root reads notes as "kept"
    And every block of the device is held, in a log, waiting for reads, or free
    When the read finishes
    And the server commits
    Then the blocks main's root used, and the change freed, are free
    And every block of the device is held, in a log or free

  @FOG_GEFS_803
  Scenario: While reads run in turn, a block goes back once every read that began before it was freed has finished
    When read 1 takes main's root
    And change 1 writes "first" to notes, and the server commits
    And read 1 finishes
    And read 2 takes main's root
    And change 2 writes "secnd" to notes, and the server commits
    And read 2 finishes
    And read 3 takes main's root
    And change 3 writes "third" to notes, and the server commits
    Then the blocks change 1 freed are free
    And the blocks change 2 freed are waiting for reads
    And the blocks change 3 freed are waiting for reads
    And every block of the device is held, in a log, waiting for reads, or free

  @FOG_GEFS_803
  Scenario: With no reads running, blocks a commit frees are free once it finishes
    When adm writes "fresh" to notes at offset 0 at time 300
    And the server commits
    Then no block is waiting for reads
    And every block of the device is held, in a log or free
