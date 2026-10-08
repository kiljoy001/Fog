@fog_gefs
Feature: A store commits all of its state at once by writing its superblocks, after 9front's gefs
  A store is a device holding arenas and a tree of snapshots. Reaming a device commits a snapshot
  tree naming an empty snapshot and main, forked from it, at generation 1. A commit makes each
  changed mutable tree a new snapshot, returns the blocks freed in the generation being written,
  logs the frees of blocks older generations used, ends each arena's log with a barrier, and writes
  the arenas' first headers; it becomes durable when the first superblock, at the device's first
  block, names them, and the backup at its last block follows, then the arenas' second headers.
  Only then do blocks the previous root used become free. Opening a device reads the first
  superblock, or the backup when the first is unusable, and replays each arena's log to the
  committed generation. Blocks written after it are free again; whatever was not committed is gone.

  Keys here are written "key N", for the three bytes 10, N's high byte and N's low byte.

  @FOG_GEFS_401
  Scenario: Reaming a device commits a snapshot tree at generation 1
    Given a device of 200 blocks reamed with 2 arenas
    Then both superblocks name 2 arenas and a snapshot tree 1 high, committed at generation 1
    When the device is opened
    Then the store is at generation 1 and main holds nothing
    And every block of the device is held, in a log or free

  @FOG_GEFS_402
  Scenario: Committed changes are there when the device is opened again, and changes after them are not
    Given a device of 400 blocks reamed with 2 arenas
    When main is given keys 1 to 50 and the store commits
    And main is given keys 51 to 60
    And the device is opened
    Then the store is at generation 2 and main holds keys 1 to 50
    And every block of the device is held, in a log or free

  @FOG_GEFS_402
  Scenario: Blocks a commit stops using are free once it has committed, and stay free when opened again
    Given a device of 400 blocks reamed with 2 arenas
    When main is given keys 1 to 50 and the store commits
    And main is given keys 1 to 50 and the store commits
    And main has keys 1 to 25 deleted and the store commits
    Then every block of the device is held, in a log or free
    When the device is opened
    Then the store is at generation 4 and main holds keys 26 to 50
    And every block of the device is held, in a log or free

  @FOG_GEFS_402
  Scenario: Generations and qids carry on where they were when a device is opened again
    Given a device of 200 blocks reamed with 2 arenas
    When 3 qids are taken and the store commits
    And the device is opened
    Then the next qid is 4
    And main writes in generation 5

  @FOG_GEFS_402
  Scenario: Commits compress allocation logs that have doubled
    Given a device of 400 blocks reamed with 2 arenas
    When main is given key 1 1100 times, the store committing after every 100
    Then each arena's log is 1 block long
    And every block of the device is held, in a log or free

  @FOG_GEFS_403
  Scenario Outline: Opening falls back to the backup superblock when the first is unusable
    Given a device of 400 blocks reamed with 2 arenas
    When main is given keys 1 to 10 and the store commits
    And the <which> superblock is <change>
    And the device is opened
    Then <outcome>

    Examples:
      | which  | change                      | outcome                                          |
      | first  | damaged                     | the store is at generation 2 and main holds keys 1 to 10 |
      | backup | damaged                     | the store is at generation 2 and main holds keys 1 to 10 |
      | first  | written by another version  | the store is at generation 2 and main holds keys 1 to 10 |
      | both   | damaged                     | opening fails with "corrupt superblock"          |

  @FOG_GEFS_403
  Scenario Outline: Opening refuses a store made for other block sizes
    Given a device of 200 blocks reamed with 2 arenas
    When both superblocks are rewritten claiming <size>
    And the device is opened
    Then opening fails with "incompatible block size"

    Examples:
      | size                       |
      | blocks of 8192 bytes       |
      | buffers of 4000 bytes      |

  @FOG_GEFS_404
  Scenario: A crash at any write of a commit opens as the generation before it or the one it commits
    Given a device of 400 blocks reamed with 2 arenas
    When main is given keys 1 to 20 and the store commits
    And main is given keys 21 to 40
    And main has keys 1 to 10 deleted
    Then a crash after any number of the next commit's writes opens as generation 2 or 3, as committed, every block held, in a log or free

  @FOG_GEFS_404
  Scenario: A commit writes its superblocks after its arena headers, flushing between them
    Given a device of 200 blocks reamed with 2 arenas
    When the device's record is cleared and the store commits
    Then the device recorded: write, write, flush, write, write, flush, write, flush, write, flush, write, write, flush
