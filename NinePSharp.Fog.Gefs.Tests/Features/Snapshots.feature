@fog_gefs
Feature: Snapshots keep older states of a tree, and deadlists free what only they held, after 9front's gefs
  Each label names a snapshot. Main is mutable: every commit that changed it makes its state a new
  snapshot, and the one before is deleted unless another label or a fork keeps it. Naming a tree
  keeps its current state; forking it starts a mutable tree of its own that shares its blocks. A
  block is held from the snapshot it was born in until the one that freed it, which lists it on a
  deadlist by its birth. Deleting a snapshot frees what it alone held: the blocks its successor
  freed that were born after the snapshot before it, and, when it has no successor, its own blocks
  born then. What the older snapshot still holds passes to the successor's deadlists, or stays with
  the older snapshot.

  Keys here are written "key N", for the three bytes 10, N's high byte and N's low byte. Every
  value given is new and 400 bytes long, so a tree holds the values given to it, or to the tree it
  was named from, last, and a hundred keys need a tree two blocks high.

  @FOG_GEFS_501
  Scenario: Reaming makes main, a mutable tree forked from an empty snapshot that cannot be deleted
    Given a device of 1600 blocks reamed with 2 arenas
    Then main holds nothing
    And empty holds nothing
    And empty's snapshot has no predecessor, no successor, no base, 1 label and 1 fork
    And main's snapshot has no predecessor, no successor, base empty, 1 label and 0 forks
    And deleting empty fails with "snap -- reserved name"
    And every block of the device is held, in a log or free

  @FOG_GEFS_502
  Scenario: A mutable tree's commits leave no snapshot behind when nothing names it, and a commit without changes leaves it be
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 120 and the store commits
    And main is given keys 1 to 120 and the store commits
    And main is given keys 1 to 120 and the store commits
    Then main's snapshot has no predecessor
    And main holds keys 1 to 120
    And a commit without changes leaves main's snapshot where it was
    And every block of the device is held, in a log or free

  @FOG_GEFS_503
  Scenario: A snapshot keeps a tree as it was when named, also once the device is opened again
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 120 and the store commits
    And main is snapshotted as monday
    And main is given keys 121 to 160
    And main has keys 1 to 10 deleted and the store commits
    Then monday holds keys 1 to 120
    And main holds keys 11 to 160
    And main's snapshot follows monday's
    And every block of the device is held, in a log or free
    When the device is opened
    Then monday holds keys 1 to 120
    And main holds keys 11 to 160
    And every block of the device is held, in a log or free

  @FOG_GEFS_503
  Scenario: A tree opened from a named snapshot cannot be changed
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 5 and the store commits
    And main is snapshotted as monday
    Then giving monday key 1 fails with "snap -- is read only"

  @FOG_GEFS_504
  Scenario: Deleting a snapshot frees what only it held
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 120 and the store commits
    And main is snapshotted as monday
    And main is given keys 1 to 40 and the store commits
    And monday is deleted and the store commits
    Then monday does not exist
    And main holds keys 1 to 120
    And main's snapshot has no predecessor
    And every block of the device is held, in a log or free

  @FOG_GEFS_504
  Scenario: Deleting the middle of three snapshots passes to the newest what the oldest still holds
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 120 and the store commits
    And main is snapshotted as a
    And main is given keys 1 to 40 and the store commits
    And main is snapshotted as b
    And main is given keys 41 to 80 and the store commits
    And b is deleted and the store commits
    Then a holds keys 1 to 120
    And main holds keys 1 to 120
    And main's snapshot follows a's
    And every block of the device is held, in a log or free
    When a is unmounted
    And a is deleted and the store commits
    Then main holds keys 1 to 120
    And main's snapshot has no predecessor
    And every block of the device is held, in a log or free
    When the device is opened
    Then main holds keys 1 to 120
    And every block of the device is held, in a log or free

  @FOG_GEFS_505
  Scenario: A fork and the tree it forked from change on their own
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 120 and the store commits
    And main is forked as f
    And f is given keys 121 to 140 and the store commits
    And main is given keys 1 to 5 and the store commits
    Then f holds keys 1 to 140
    And main holds keys 1 to 120
    And every block of the device is held, in a log or free
    When the device is opened
    Then f holds keys 1 to 140
    And main holds keys 1 to 120
    And every block of the device is held, in a log or free

  @FOG_GEFS_505
  Scenario: Deleting the newest snapshots of a fork frees what they held after the ones before them
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 120 and the store commits
    And main is forked as f
    And f is given keys 121 to 140 and the store commits
    And f is given a data block for file 1 and the store commits
    And f is snapshotted as f1
    And f is given keys 1 to 10 and the store commits
    And f is given keys 141 to 160 and the store commits
    And f is given a data block for file 1 and the store commits
    Then f holds keys 1 to 160
    And f1 holds keys 1 to 140
    And main holds keys 1 to 120
    And every block of the device is held, in a log or free
    When f is unmounted
    And f is deleted and the store commits
    Then f does not exist
    And f1 holds keys 1 to 140
    And main's snapshot has no predecessor, no successor, base empty, 1 label and 1 fork
    And every block of the device is held, in a log or free
    When f1 is unmounted
    And f1 is deleted and the store commits
    Then f1 does not exist
    And main holds keys 1 to 120
    And main's snapshot has no predecessor, no successor, base empty, 1 label and 0 forks
    And every block of the device is held, in a log or free

  @FOG_GEFS_505
  Scenario: A snapshot a fork was made from stays while the fork lives, and goes with it
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 120 and the store commits
    And main is snapshotted as monday
    And main is given keys 1 to 40 and the store commits
    And monday is forked as f
    And f is given keys 121 to 140 and the store commits
    And monday is deleted and the store commits
    Then monday does not exist
    And f holds keys 1 to 140
    And main's snapshot follows the one f was forked from
    And every block of the device is held, in a log or free
    When f is unmounted
    And f is deleted and the store commits
    Then main holds keys 1 to 120
    And main's snapshot has no predecessor
    And every block of the device is held, in a log or free

  @FOG_GEFS_505
  Scenario: Deleting a fork keeps the file data it shared with the snapshot it forked from
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given a data block for file 1 and the store commits
    And main is forked as f
    And f is given keys 1 to 20 and the store commits
    And f is unmounted
    And f is deleted and the store commits
    Then main holds nothing
    And every block of the device is held, in a log or free

  @FOG_GEFS_506
  Scenario Outline: Naming, deleting and opening snapshots refuse what cannot be done
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 5 and the store commits
    And main is snapshotted as monday
    Then <action> fails with "<error>"

    Examples:
      | action                                   | error                        |
      | snapshotting main as monday              | snap -- already exists       |
      | forking main as monday                   | snap -- already exists       |
      | snapshotting main as empty               | snap -- reserved name        |
      | deleting dump                            | snap -- reserved name        |
      | deleting tuesday                         | snap -- does not exist       |
      | deleting main                            | snap -- is currently mounted |
      | mounting tuesday                         | snap -- does not exist       |
      | snapshotting tuesday as wednesday        | snap -- does not exist       |
      | snapshotting main as a name of 246 bytes | name too long                |

  @FOG_GEFS_506
  Scenario: A label may be as long as an entry's name
    Given a device of 1600 blocks reamed with 2 arenas
    When main is snapshotted as a name of 245 bytes
    Then the name of 245 bytes holds nothing

  @FOG_GEFS_506
  Scenario: A tree with uncommitted changes cannot be unmounted
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 5
    Then unmounting main fails with "snap -- has uncommitted changes"

  @FOG_GEFS_506
  Scenario: A label naming a snapshot the tree does not hold is a damaged store
    Given a device of 1600 blocks reamed with 2 arenas
    When a label ghost is made naming generation 999
    Then mounting ghost fails with "internal error"

  @FOG_GEFS_507
  Scenario: A deadlist longer than a block continues in another, and both are written at the commit
    Given a device of 1600 blocks reamed with 2 arenas
    When main's tree lists 2100 blocks born in generation 1 as freed, and the store commits
    Then no deadlist is open
    And main's deadlist for generation 1 lists those 2100 blocks in 2 blocks
    When the device is opened
    Then main's deadlist for generation 1 lists those 2100 blocks in 2 blocks

  @FOG_GEFS_508
  Scenario: A crash at any write of a commit that deletes a snapshot opens as the generation before it or the one it commits
    Given a device of 1600 blocks reamed with 2 arenas
    When main is given keys 1 to 120 and the store commits
    And main is snapshotted as monday
    And main is given keys 1 to 40 and the store commits
    And the device is reopened
    And main is given keys 41 to 80
    And monday is deleted
    Then a crash after any number of the next commit's writes opens as generation 3 or 4, as committed, every block held, in a log or free
