@fog_gefs
Feature: Keys, values and messages are packed as 9front's gefs packs them
  Gefs keeps a whole file system in one sorted key-value store. A key is a type byte and its data,
  and keys sort by their bytes, a key before any longer key it begins. A directory entry is keyed
  by its parent's qid and its name; its value is the packed entry. All numbers are big-endian.
  Changes are messages addressed to a key: insert replaces the value, delete and the deferred frees
  remove it, wstat changes the fields its flags name and bumps the qid version, and the snapshot
  messages relink a tree or adjust its counts. Fog's format differs from 9front's in two ways: a
  block pointer carries the block's SHA-256 hash rather than a 64-bit MetroHash, and the
  superblock names Fog's format, so neither can be mistaken for the other.

  @FOG_GEFS_001
  Scenario Outline: Keys sort by their bytes, a key before any longer key it begins
    When key <a> is compared with key <b>
    Then it sorts <order>

    Examples:
      | a      | b      | order  |
      | 01     | 02     | before |
      | 02     | 01     | after  |
      | 01     | 0100   | before |
      | 0100   | 01     | after  |
      | 0102ff | 0102ff | level  |

  @FOG_GEFS_002
  Scenario: A directory entry's key is its type, its parent's qid and its terminated name
    When the entry key for "foo" in directory 2 is packed
    Then its bytes are 01 0000000000000002 0003 666f6f 00
    And it unpacks to directory 2 and the name "foo"

  @FOG_GEFS_002
  Scenario: The entries of one directory sort together, in name order
    When the entry keys for "b" in directory 2, "z" in directory 1 and "a" in directory 2 are sorted
    Then they are in the order "z" in directory 1, "a" in directory 2, "b" in directory 2

  @FOG_GEFS_002
  Scenario Outline: A name is at most 245 bytes, so that its entry key fits
    When the entry key for a name of <length> bytes in directory 2 is packed
    Then packing <result>

    Examples:
      | length | result                      |
      | 245    | succeeds                    |
      | 246    | fails with "name too long"  |

  @FOG_GEFS_002
  Scenario: A snapshot's key is its type and its id
    When the snapshot key for snapshot 40 is packed
    Then its bytes are 04 0000000000000028

  @FOG_GEFS_003
  Scenario: A directory entry's value is 61 bytes and unpacks to the entry that was packed
    Given an entry "notes" with qid 7 version 3, mode 0644, length 11, atime 5, mtime 6, user 1, group 2 and muid 3, flagged 5
    When its key and value are packed
    Then the value is 61 bytes long
    And they unpack to the same entry

  @FOG_GEFS_004
  Scenario: A block pointer is the block's address, SHA-256 hash and birth generation
    Given a block holding "hello" at address 32768 born in generation 9
    When its pointer is packed
    Then the pointer is 48 bytes long
    And its hash is 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
    And it unpacks to address 32768 and generation 9 with the same hash

  @FOG_GEFS_005
  Scenario: A snapshot tree's entry unpacks to the tree that was packed
    Given a tree with 1 reference, 2 labels, height 3, flags 1, generation 4, predecessor 5, successor 6, base 7 and its root at address 49152
    When its entry is packed
    Then it unpacks to the same tree

  @FOG_GEFS_006
  Scenario Outline: A message changes the value at its key
    Given the value "old" at key 0a
    When a <message> message for key 0a with value "new" is applied
    Then the key <result>

    Examples:
      | message     | result         |
      | insert      | holds "new"    |
      | delete      | holds no value |
      | clear block | holds no value |
      | clobber     | holds no value |

  @FOG_GEFS_006
  Scenario: A message of no known kind is refused
    Given the value "old" at key 0a
    When a message of kind 0 for key 0a with value "new" is applied
    Then applying fails with "invalid message"

  @FOG_GEFS_007
  Scenario Outline: A wstat changes only the fields its flags name, and bumps the qid version
    Given an entry "notes" with qid 7 version 3, mode 0644, length 11, atime 5, mtime 6, user 1, group 2 and muid 3
    When a wstat setting <fields> is applied to it
    Then the entry has <changed>, qid version 4, and every other field as it was

    Examples:
      | fields                     | changed                    |
      | length 99                  | length 99                  |
      | mode 0600                  | mode 0600                  |
      | mode d0755                 | mode d0755 and qid type 80 |
      | mtime 70 and atime 80      | mtime 70 and atime 80      |
      | user 9, group 8 and muid 7 | user 9, group 8 and muid 7 |
      | nothing                    | nothing                    |

  @FOG_GEFS_007
  Scenario: A wstat carrying bytes its flags do not account for is refused
    Given an entry "notes" with qid 7 version 3, mode 0644, length 11, atime 5, mtime 6, user 1, group 2 and muid 3
    When a wstat setting length 99 is applied to it, with 4 bytes too many
    Then applying fails with "malformed stat"

  @FOG_GEFS_008
  Scenario Outline: Snapshot messages relink a tree or adjust its counts
    Given a tree with 1 reference, 2 labels, height 3, flags 1, generation 4, predecessor 5, successor 6, base 7 and its root at address 49152
    When a <message> message linking snapshot 40, changing labels by <labels> and references by <references>, is applied to it
    Then the tree has <link>, 2 labels changed by <labels> and 1 reference changed by <references>

    Examples:
      | message | labels | references | link           |
      | relink  | 1      | 0          | successor 40   |
      | reprev  | 0      | 1          | predecessor 40 |
      | incref  | -2     | -1         | no new link    |

  @FOG_GEFS_008
  Scenario: A snapshot message cannot make a count negative
    Given a tree with 1 reference, 2 labels, height 3, flags 1, generation 4, predecessor 5, successor 6, base 7 and its root at address 49152
    When an incref message linking snapshot 40, changing labels by -3 and references by 0, is applied to it
    Then applying fails with "negative snapshot count"

  @FOG_GEFS_009
  Scenario Outline: A superblock unpacks to the file system state that was packed
    Given a file system with <arenas> arenas, flags 3, next qid 100, next generation 200 and sync generation 150
    When its superblock is packed into a block
    Then the block starts with "fogfs001"
    And it unpacks to the same file system state

    Examples:
      | arenas |
      | 0      |
      | 4      |
      | 256    |

  @FOG_GEFS_009
  Scenario Outline: A damaged or foreign superblock is refused
    Given a file system with 4 arenas, flags 3, next qid 100, next generation 200 and sync generation 150
    When its superblock is packed into a block and <damage>
    Then unpacking fails with "<error>"

    Examples:
      | damage                                      | error               |
      | byte 40 is changed                          | corrupt superblock  |
      | its first 8 bytes are replaced by gefs9.00  | unknown fs version  |
      | its arena count is changed to 1000          | corrupt superblock  |
      | its arena count is changed to -1            | corrupt superblock  |
