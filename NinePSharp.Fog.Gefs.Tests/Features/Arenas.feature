@fog_gefs
Feature: Arenas divide a device's blocks and log every allocation, after 9front's gefs
  A device is a file of 16 KiB blocks. Formatting keeps its first and last blocks for superblocks
  and divides the rest between arenas. Each arena starts with a pair of identical headers and
  allocates from its free ranges: from the top for tree blocks, from the bottom for sequential
  data, holding back a reserve for when the device is nearly full. Every allocation and free is
  appended to the arena's log, which grows through blocks the arena allocates from itself. A
  sync writes a barrier for the generation it commits; reopening an arena replays its log up to
  that barrier, so whatever was allocated after the last commit is free again. A header found
  damaged or stale falls back to its twin.

  Blocks here are numbered from the start of the device, and ranges of them are inclusive.

  @FOG_GEFS_301
  Scenario: Formatting divides the device between arenas, each with a pair of headers and a log
    Given a device of 134 blocks formatted with 2 arenas
    Then arena 0 has its headers at blocks 1 and 2 and 64 blocks of data from block 3
    And arena 1 has its headers at blocks 67 and 68 and 64 blocks of data from block 69
    And each arena's headers are the same, name its log at the first block of its data, and count 1 block used
    And each arena's log frees its data, takes the log's own block and ends with a barrier for generation 0
    When the arenas are reopened at generation 0
    Then arena 0 has blocks 3 taken
    And arena 1 has blocks 69 taken

  @FOG_GEFS_301
  Scenario Outline: Formatting needs room for at least eight blocks of data in each arena
    Given a device of <blocks> blocks
    When it is formatted with 2 arenas
    Then formatting <result>

    Examples:
      | blocks | result                          |
      | 22     | succeeds                        |
      | 21     | fails with "device too small"   |

  @FOG_GEFS_302
  Scenario Outline: Freed blocks merge with the free ranges beside them
    Given free ranges <ranges>
    When blocks <freed> are freed
    Then the free ranges are <after>

    Examples:
      | ranges    | freed | after           |
      | 3-5, 9-10 | 6-8   | 3-10            |
      | 3-5, 9-10 | 6-6   | 3-6, 9-10       |
      | 3-5, 9-10 | 8-8   | 3-5, 8-10       |
      | 3-5       | 1-1   | 1-1, 3-5        |
      | 3-5       | 7-7   | 3-5, 7-7        |
      | 3-5, 9-10 | 11-11 | 3-5, 9-11       |
      | 3-5, 9-10, 14-15 | 6-8 | 3-10, 14-15 |

  @FOG_GEFS_302
  Scenario Outline: Taking blocks splits the free range they come from
    Given free ranges <ranges>
    When blocks <taken> are taken
    Then the free ranges are <after>

    Examples:
      | ranges    | taken | after      |
      | 3-10      | 3-4   | 5-10       |
      | 3-10      | 9-10  | 3-8        |
      | 3-10      | 5-6   | 3-4, 7-10  |
      | 3-10, 12-12 | 3-10 | 12-12     |
      | 3-5, 9-10 | 9-9   | 3-5, 10-10 |

  @FOG_GEFS_302
  Scenario Outline: Free ranges refuse to free what is free or take what is not
    Given free ranges 3-5
    When blocks <blocks> are <done>
    Then the free ranges refuse it with "<error>"

    Examples:
      | blocks | done  | error             |
      | 4-4    | freed | block freed twice |
      | 3-3    | freed | block freed twice |
      | 5-6    | freed | block freed twice |
      | 2-3    | freed | block freed twice |
      | 6-6    | taken | block not free    |
      | 4-6    | taken | block not free    |
      | 2-3    | taken | block not free    |

  @FOG_GEFS_302
  Scenario: Allocation takes the top of the last free range or the bottom of the first
    Given free ranges 3-5, 9-10
    Then the lowest free block is 3 and the highest 10

  @FOG_GEFS_303
  Scenario: An arena allocates tree blocks from the top of its free space and data from the bottom
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates 3 blocks, and then 2 in sequence
    Then it gave blocks 66, 65, 64, 4 and 5
    And arena 0 counts 6 blocks used

  @FOG_GEFS_303
  Scenario: An arena holds back its reserve unless told to use it, and then runs out
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates 31 blocks
    Then arena 0 counts 32 blocks used
    And arena 0 refuses another block, keeping its reserve
    When arena 0 is allowed its reserve and allocates 32 blocks
    Then arena 0 counts 64 blocks used
    And allocating another block from arena 0 fails with "emergency blocks exhausted"

  @FOG_GEFS_304
  Scenario: Every allocation and free is logged, a block in one word and a range in two
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates 2 blocks and frees block 66
    Then arena 0's log reads: free 3-66, take 3, barrier 0, take 66, take 65, free 66

  @FOG_GEFS_303
  Scenario: Tree blocks are allocated from one arena and then the next in turn, 2048 at a time
    Given a device of 4200 blocks formatted with 2 arenas
    When 2047 tree blocks are allocated
    Then all of them came from arena 0, and the next comes from arena 1

  @FOG_GEFS_303
  Scenario: When the arena whose turn it is has only its reserve left, tree blocks come from another
    Given a device of 4200 blocks formatted with 2 arenas
    When 2047 tree blocks are allocated
    And arena 1 allocates all but its reserve
    Then the next tree block comes from arena 0

  @FOG_GEFS_304
  Scenario Outline: Reopening an arena replays its log up to the barrier of the committed generation
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates 3 blocks
    And the arenas sync generation 1
    And arena 0 allocates 2 blocks and frees block 66
    And the arenas sync generation 2 without committing it
    And the arenas are reopened at generation <generation>
    Then arena 0 has blocks <taken> taken and counts <used> blocks used

    Examples:
      | generation | taken                 | used |
      | 1          | 3 and 64-66           | 4    |
      | 2          | 3 and 62-65           | 5    |

  @FOG_GEFS_304
  Scenario: An arena reopened at an older generation logs on from that generation's barrier
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates 3 blocks
    And the arenas sync generation 1
    And arena 0 allocates 2 blocks and frees block 66
    And the arenas sync generation 2 without committing it
    And the arenas are reopened at generation 1
    And arena 0 allocates 1 block
    And the arenas sync generation 2
    And the arenas are reopened at generation 2
    Then arena 0 has blocks 3 and 63-66 taken and counts 5 blocks used

  @FOG_GEFS_304
  Scenario: Entries logged after a reopen stay uncommitted until a sync of their own commits
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates 3 blocks
    And the arenas sync generation 1
    And arena 0 allocates 2 blocks and frees block 66
    And the arenas sync generation 2 without committing it
    And the arenas are reopened at generation 1
    And arena 0 allocates 1 block and frees block 65
    And the arenas sync generation 2 without committing it
    And the arenas are reopened at generation 1
    Then arena 0 has blocks 3 and 64-66 taken and counts 4 blocks used

  @FOG_GEFS_304
  Scenario: Reopening an arena at a generation its log never reached fails
    Given a device of 134 blocks formatted with 2 arenas
    When the arenas sync generation 1
    And the arenas are reopened with generation 1's headers at generation 2
    Then reopening fails with "internal error"

  @FOG_GEFS_305
  Scenario Outline: A log block takes entries until one and the word chaining to the next would not fit
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates and frees a block <times> times
    Then arena 0's log is <blocks> blocks long and arena 0 counts <blocks> blocks used

    Examples:
      | times | blocks |
      | 1016  | 1      |
      | 1017  | 2      |

  @FOG_GEFS_305
  Scenario: A log too long for its block continues in a block the arena allocates for it
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates and frees a block 1100 times
    And the arenas sync generation 1
    Then arena 0's log is 2 blocks long and arena 0 counts 2 blocks used
    And arena 0's log holds 2205 entries
    When the arenas are reopened at generation 1
    Then arena 0's log is 2 blocks long and arena 0 counts 2 blocks used
    And arena 0 has blocks 3 and 65 taken

  @FOG_GEFS_306
  Scenario: Compressing a log rewrites it as the arena's free ranges, and frees the old log only after the sync
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates and frees a block 1100 times
    And arena 0 allocates 3 blocks
    And the arenas sync generation 1, compressing logs that have doubled
    Then arena 0's log is 1 block long and reads: free 5-62, barrier 1, free 3, free 65
    And arena 0 has blocks 4, 63-64 and 66 taken and counts 4 blocks used
    When the arenas are reopened at generation 1
    Then arena 0 has blocks 3-4 and 63-66 taken

  @FOG_GEFS_306
  Scenario: A log is compressed only once it has doubled since it was last compressed
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates and frees a block 1100 times
    And the arenas sync generation 1
    And the arenas are reopened at generation 1
    And the arenas sync generation 2, compressing logs that have doubled
    Then arena 0's log is 2 blocks long and arena 0 counts 2 blocks used

  @FOG_GEFS_306
  Scenario: A full arena keeps its log as it is
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates and frees a block 1100 times
    And arena 0 is allowed its reserve and allocates 62 blocks
    And the arenas sync generation 1, compressing logs that have doubled
    Then arena 0's log is 2 blocks long and arena 0 counts 64 blocks used

  @FOG_GEFS_306
  Scenario Outline: A compressed log takes as many blocks as its free ranges need
    Given a device of 2300 blocks formatted with 1 arena
    When arena 0's free space is left as <ranges> single blocks
    And arena 0's log is compressed and the arenas sync generation 1
    Then arena 0's log is <blocks> blocks long
    When the arenas are reopened at generation 1
    Then arena 0's free space is as it was

    Examples:
      | ranges | blocks |
      | 1018   | 1      |
      | 1019   | 2      |
      | 1100   | 2      |

  @FOG_GEFS_306
  Scenario: An arena whose log cannot grow refuses the allocation that needs it to
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates and frees a block 985 times
    And arena 0 is allowed its reserve and allocates 62 blocks
    Then allocating another block from arena 0 fails with "file system full"

  @FOG_GEFS_307
  Scenario Outline: An arena whose first header is damaged or stale loads from its second
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates 3 blocks
    And the arenas sync generation 1
    And arena 0's <header> header is <change>
    And the arenas are reopened at generation 1
    Then <outcome>

    Examples:
      | header | change                 | outcome                                         |
      | first  | damaged                | arena 0 has blocks 3 and 64-66 taken            |
      | second | damaged                | arena 0 has blocks 3 and 64-66 taken            |
      | first  | left as it was at format | arena 0 has blocks 3 and 64-66 taken          |
      | both   | damaged                | reopening fails with "internal error"           |

  @FOG_GEFS_308
  Scenario: Tree blocks are written to the device and read back checked
    Given a device of 2000 blocks formatted with 2 arenas
    When a tree on it is given 300 keys of 250 bytes with 500-byte values, 6 at a time
    Then every key looks up as it was given
    And a block of the tree read with a byte changed fails with "block contents corrupted"

  @FOG_GEFS_308
  Scenario: Blocks freed in the generation being written are reused after reclaiming; older ones are only reported
    Given a device of 134 blocks formatted with 2 arenas, writing generation 5
    When a block born in generation 5 in each arena and a block born in generation 4 are allocated and freed
    Then none is free yet, and the block born in generation 4 is reported for its deadlist
    When the arenas reclaim
    Then the blocks born in generation 5 are free again, each in its own arena, and the block born in generation 4 is not
    When the arenas reclaim
    Then arena 0 counts 2 blocks used

  @FOG_GEFS_308
  Scenario: Allocation moves to the next arena when one is full, and the device fills
    Given a device of 134 blocks formatted with 2 arenas
    When 62 tree blocks are allocated
    Then 31 came from each arena
    And allocating another tree block fails with "file system full"

  @FOG_GEFS_309
  Scenario Outline: A crash at any write of a sync leaves the arenas as one generation or the other
    Given a device of 134 blocks formatted with 2 arenas
    When arena 0 allocates 3 blocks
    And the arenas sync generation 1
    And arena 0 allocates 2 blocks and arena 1 allocates 1 block
    And the arenas sync generation 2, the device crashing after <writes> of its writes
    And the arenas are reopened at the generation of the superblock last written
    Then the arenas hold what generation <generation> allocated

    Examples:
      | writes | generation |
      | 0      | 1          |
      | 1      | 1          |
      | 2      | 1          |
      | 3      | 1          |
      | 4      | 1          |
      | 5      | 2          |
      | 6      | 2          |

  @FOG_GEFS_309
  Scenario: A sync has each step reach the disk before the next
    Given a device of 134 blocks formatted with 2 arenas
    When the device's record is cleared and the arenas sync generation 1
    Then the device saw: write, write, flush, write, write, flush, write, write, write, flush

  @FOG_GEFS_310
  Scenario: A device file is made at its full size and reads back what was written
    Given a new device file of 40 blocks
    Then the file is 40 blocks long
    When block 5 of it is written, and it is closed and opened again
    Then block 5 reads back as written
    And opening it a second time while it is open fails
    And reading half a block before its end fails with "i/o error"

  @FOG_GEFS_310
  Scenario: Opening a device file refuses one that is not a whole number of blocks
    Given a file of 40 blocks and 100 bytes
    Then opening it as a device fails with "device size is not a whole number of blocks"
    And it can be opened again afterwards
