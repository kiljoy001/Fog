@fog_gefs
Feature: The Bε tree buffers messages in its pivots and flushes them toward its leaves as 9front's gefs does
  An upsert puts its messages, sorted stably by key, into the root's buffer when the root is a pivot
  with room. Otherwise it walks down from the root, each time toward the child whose messages fill
  the most of its parent's buffer, until it reaches a pivot with room or a leaf. It then rewrites
  that path from the bottom up: a block takes the messages its parent pulls down, and a block too
  full to take them splits in two. A rewritten block may merge with a sibling, or rotate entries
  with one, and a root left with one child gives way to that child. Every block an upsert replaces
  is freed, and so is a file's data block when the entry pointing at it is replaced or cleared.
  A lookup or a scan applies the messages still buffered above a key to what its leaf holds.

  Keys here are written "key N", for the three bytes 10, N's high byte and N's low byte. Unless a
  scenario says otherwise, each value is 500 bytes, so a leaf holds 32 entries before it splits.

  @FOG_GEFS_201
  Scenario: An empty tree is a leaf with nothing in it
    Given an empty tree
    Then the tree is 1 high and its root is a leaf of 0 entries
    And looking up key 1 finds nothing
    And a scan of the whole tree gives nothing

  @FOG_GEFS_201
  Scenario: Values given to a leaf root are found and scanned in key order
    Given an empty tree
    When keys 30, 10 and 20 are inserted one at a time
    Then the tree is 1 high and its root is a leaf of 3 entries
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_201
  Scenario Outline: Messages for one key apply in the order they were given
    Given an empty tree
    When key 1 is given <first> and then <second> in one upsert
    Then looking up key 1 <result>

    Examples:
      | first                | second               | result                     |
      | an insert of value a | an insert of value b | finds value b              |
      | an insert of value a | a delete             | finds nothing              |
      | an insert of value a | a clobber            | finds nothing              |
      | a clobber            | an insert of value b | finds value b              |

  @FOG_GEFS_201
  Scenario: The empty key holds a value like any other
    Given an empty tree
    When the empty key is given value a, and then value b in another upsert
    Then looking up the empty key finds value b

  @FOG_GEFS_202
  Scenario: A leaf root too full for a message splits in two under a new root
    Given an empty tree
    When keys 1 to 32 are inserted one at a time
    Then the tree is 1 high and its root is a leaf of 32 entries
    When key 33 is inserted
    Then the tree is 2 high
    And its root points at a leaf of 17 entries from key 1 and a leaf of 15 entries from key 18
    And its root buffers messages for key 33
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_202
  Scenario: A split takes in the messages that sort among the leaf's values
    Given an empty tree
    When keys 1 to 32 are inserted one at a time
    And key 0 is inserted
    Then the tree is 2 high
    And its root points at a leaf of 17 entries from key 0 and a leaf of 16 entries from key 17
    And its root buffers no messages
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_202
  Scenario Outline: A split takes in only as many of its parent's messages as each half has room for
    Given a tree 2 high whose root points at leaves of keys 40 to 102 by twos and 200 to 201
    And its root buffers <size>-byte inserts of keys <keys>
    When keys 300 and 301 are inserted in one upsert
    Then its root points at leaves of <after>
    And its root buffers messages for keys <left>, 300 and 301
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

    Examples:
      | size | keys              | after                                                                   | left |
      | 512  | 1 to 15           | 16 entries from key 1, 30 entries from key 44 and 2 entries from key 200  | 15   |
      | 500  | 73 to 101 by twos | 17 entries from key 40, 29 entries from key 73 and 2 entries from key 200 | 101  |

  @FOG_GEFS_202
  Scenario: A split leaves at least two entries in each half
    Given an empty tree
    When keys 1 to 18 are inserted one at a time
    And keys 100 to 114 are inserted in one upsert
    Then its root points at leaves of 16 entries from key 1 and 2 entries from key 17
    And its root buffers messages for keys 100 to 114
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_202
  Scenario: A split moves to the right half once the left has exactly half a block
    Given an empty tree
    When keys 1 to 20 are inserted one at a time, key 17 with a 36-byte value
    And keys 100 to 114 are inserted in one upsert
    Then its root points at leaves of 17 entries from key 1 and 3 entries from key 18
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_202
  Scenario: A full pivot root splits in half, its buffered messages going with the pointers they will flush to
    Given a tree 2 high whose root points at 130 one-entry leaves for keys 1 to 130
    And its root buffers 500-byte inserts of keys 66 and 131 to 143
    When keys 200 to 202 are inserted in one upsert
    Then the tree is 3 high
    And its root points at pivots of 65 pointers from key 1 and 65 pointers from key 66
    And the pivot from key 66 buffers messages for key 66
    And its root buffers messages for keys 200 to 202
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_203
  Scenario: A pivot root with room takes an upsert into its buffer, after the messages already there for the same key
    Given an empty tree
    When keys 1 to 33 are inserted one at a time
    And keys 33, 5 and 20 are inserted in one upsert
    Then its root buffers messages for keys 5, 20, 33 and 33
    And only the root was rewritten, and only the old root was freed
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_204
  Scenario: A root whose buffer is full flushes the messages for the child they fill it most for
    Given an empty tree
    When keys 1 to 33 are inserted one at a time
    And keys 2, 3, 4 and 34 to 45 are inserted one at a time
    Then its root buffers messages for keys 2, 3, 4 and 33 to 45
    When key 46 is inserted
    Then its root buffers messages for keys 2, 3, 4 and 46
    And its root points at the same leaf from key 1 and a leaf of 28 entries from key 18
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_205
  Scenario: Siblings small enough to share a block merge, and a root left with one child gives way to it
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 6
    And its root buffers 500-byte inserts of keys 0 to 2
    When keys 20 to 33 are inserted in one upsert
    Then the tree is 1 high and its root is a leaf of 21 entries
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_205
  Scenario: Two of three siblings merge, and the root keeps the merged block and the third
    Given a tree 2 high whose root points at leaves of keys 1 to 2, 3 to 6 and 20 to 30
    And its root buffers 500-byte inserts of keys 0 to 2
    When keys 40 to 53 are inserted in one upsert
    Then its root points at leaves of 7 entries from key 0 and 11 entries from key 20
    And its root buffers messages for keys 40 to 53
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_205
  Scenario: A root whose whole buffer flushes to one of its two children keeps both
    Given an empty tree
    When keys 1 to 33 are inserted one at a time
    And keys 34 to 48 are inserted one at a time
    And key 49 is inserted
    Then its root points at leaves of 17 entries from key 1 and 31 entries from key 18
    And its root buffers messages for key 49
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_205
  Scenario: A root merged down to one child with messages left in its buffer gives way once they are flushed
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 6
    And its root buffers 500-byte inserts of keys 0 to 2 and 7
    When keys 20 to 32 are inserted in one upsert
    Then the tree is 1 high and its root is a leaf of 21 entries
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_206
  Scenario: Siblings too unequal to leave alone, but too large to merge, share their entries evenly
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 14
    And its root buffers 500-byte inserts of keys 0 to 2
    When keys 20 to 33 are inserted in one upsert
    Then the tree is 2 high
    And its root points at leaves of 8 entries from key 0 and 7 entries from key 8
    And its root buffers messages for keys 20 to 33
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_206
  Scenario Outline: Siblings merge below the merge limit, and rotate only past the imbalance limit
    Given a tree 2 high whose root points at leaves of keys <leaves>, key <small> holding a <size>-byte value
    And its root buffers 500-byte inserts of keys 1
    When keys 20 to 34 are inserted in one upsert
    Then its root points at leaves of <after>
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

    Examples:
      | leaves            | small | size | after                                          |
      | 1 to 1 and 3 to 12 | 12   | 12   | 6 entries from key 1 and 5 entries from key 8  |
      | 1 to 2 and 3 to 11 | 11   | 13   | 2 entries from key 1 and 9 entries from key 3  |

  @FOG_GEFS_206
  Scenario: A pivot pulls in a message that exactly fills the room it has left
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 14
    And its root buffers 500-byte inserts of keys 0 to 2, 15 and 16
    When keys 20 to 31, and key 32 with a 17-byte value, go into one upsert
    Then its root buffers messages for keys 15, 16 and 20 to 32
    And every key looks up as it was last given and a scan gives them in key order

  @FOG_GEFS_206
  Scenario: A pivot rotates with its sibling, its buffered messages going with the pointers they will flush to
    Given a tree 3 high whose root points at a pivot over one-entry leaves for keys 1 to 2 and a pivot over one-entry leaves for keys 3 to 87, the second buffering 500-byte inserts of keys 44 and 45
    And its root buffers 500-byte inserts of keys 0 to 2
    When keys 100 to 113 are inserted in one upsert
    Then its root points at pivots of 44 pointers from key 0 and 43 pointers from key 45
    And the pivot from key 0 buffers messages for keys 0, 1, 2 and 44
    And the pivot from key 45 buffers messages for key 45
    And its root buffers messages for keys 100 to 113
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_207
  Scenario: A lookup applies the messages buffered above a key to what its leaf holds
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 6
    And its root buffers an insert of key 0, a delete of key 2, an insert of key 3, an insert of key 7, an insert of key 7 and a clobber of key 8
    Then every key looks up as it was last given and a scan gives them in key order

  @FOG_GEFS_207
  Scenario: A lookup refuses a buffered change to a key that holds no value
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 6
    And its root buffers a delete of key 9
    Then looking up key 9 fails with "internal error: missing insert"
    And scanning the whole tree fails with "internal error: broken entry"

  @FOG_GEFS_207
  Scenario: Stat changes buffered above an entry apply when it is looked up or scanned
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 6
    When the entry "notes" in directory 1, 10 bytes long and modified at 100, is inserted
    And its length is set to 20, and then its modification time to 200, in one upsert
    Then the entry looks up as 20 bytes long, modified at 200, at version 2, and a scan of directory 1 gives it so

  @FOG_GEFS_207
  Scenario: Stat changes apply to an entry in its leaf
    Given an empty tree
    When the entry "notes" in directory 1, 10 bytes long and modified at 100, is inserted
    And its length is set to 20, and then its modification time to 200, in one upsert
    Then the tree is 1 high and its root is a leaf of 1 entries
    And the entry looks up as 20 bytes long, modified at 200, at version 2, and a scan of directory 1 gives it so

  @FOG_GEFS_208
  Scenario Outline: A scan restricted to a prefix gives only the keys that begin with it
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 6
    And its root buffers an insert of key 2 and an insert of key 7
    Then a scan of keys beginning <prefix> gives <keys>

    Examples:
      | prefix | keys    |
      | 100003 | key 3   |
      | 100004 | key 4   |
      | 100007 | key 7   |
      | 11     | nothing |

  @FOG_GEFS_208
  Scenario: A scan begins after a shorter key that sorts before its prefix
    Given an empty tree
    When the keys 10, 100001, 100002 and 11 are inserted one at a time
    Then a scan of keys beginning 1000 gives the keys 100001 and 100002

  @FOG_GEFS_208
  Scenario: A scan re-entered after the tree changes continues after the last key it gave
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 6
    And its root buffers an insert of key 0, a delete of key 2, an insert of key 3, an insert of key 7 and a clobber of key 8
    When a scan of the whole tree gives 3 entries and is left
    And key 2 is inserted
    And key 4 is deleted
    And the scan is re-entered
    Then it gives keys 5, 6 and 7, and then nothing

  @FOG_GEFS_208
  Scenario: A scan that was left gives nothing until it is entered again
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 6
    When a scan of the whole tree gives 2 entries and is left
    Then it gives nothing
    When the scan is re-entered
    Then it gives keys 3, 4, 5 and 6, and then nothing

  @FOG_GEFS_208
  Scenario Outline: A scan that has run out stays finished, even when keys it would give are added
    Given a tree 2 high whose root points at a leaf of keys 1 to 2 and a leaf of keys 3 to 6
    And its root buffers an insert of key 256
    When a scan of keys beginning <prefix> gives all it has and is left
    And key <key> is inserted
    And the scan is re-entered
    Then it gives nothing

    Examples:
      | prefix | key |
      | 10     | 300 |
      | 1000   | 9   |

  @FOG_GEFS_209
  Scenario: Replacing or clearing a file's data entry frees the data block it pointed at
    Given an empty tree
    And data blocks A, B, C and D
    When the data entry for file 1 at offset 0 is inserted pointing at A
    Then block A is still in use
    When the data entry for file 1 at offset 0 is inserted pointing at B
    Then block A was freed and block B is still in use
    When the data entry for file 1 at offset 0 is cleared
    Then block B was freed
    When the data entry for file 1 at offset 0 is inserted pointing at C and then at D in one upsert
    Then block C was freed and block D is still in use
    When the data entry for file 1 at offset 0 is clobbered and its owner frees block D
    Then the data entry for file 1 at offset 0 looks up as nothing
    And every block of it is well formed, and every block it no longer uses was freed

  @FOG_GEFS_210
  Scenario: An upsert refuses a change to a key that holds no value
    Given an empty tree
    When keys 1 to 3 are inserted one at a time
    And key 9 is deleted
    Then the upsert fails with "internal error: broken entry"
    And every key looks up as it was last given and a scan gives them in key order

  @FOG_GEFS_210
  Scenario: An upsert refuses a stat change to a key a clobber has just emptied
    Given an empty tree
    When keys 1 to 3 are inserted one at a time
    And key 2 is clobbered and then given a stat change in one upsert
    Then the upsert fails with "internal error: broken entry"
    And every key looks up as it was last given and a scan gives them in key order

  @FOG_GEFS_210
  Scenario Outline: An upsert refuses a key or value larger than gefs allows
    Given an empty tree
    When a key of <key> bytes is inserted with a value of <value> bytes
    Then the upsert <result>

    Examples:
      | key | value | result                          |
      | 256 | 512   | succeeds                        |
      | 257 | 0     | fails with "key too large"      |
      | 1   | 513   | fails with "value too large"    |

  @FOG_GEFS_210
  Scenario Outline: An upsert refuses a batch that would not fit an empty buffer
    Given an empty tree
    When keys 20 to 34, and key 35 with a <size>-byte value, go into one upsert
    Then the upsert <result>

    Examples:
      | size | result                            |
      | 495  | succeeds                          |
      | 496  | fails with "upsert too large"     |

  @FOG_GEFS_210
  Scenario Outline: An upsert refuses a tree that could grow past the most levels gefs allows
    Given a leaf root recorded as <height> high
    When key 1 is inserted
    Then the upsert <result>

    Examples:
      | height | result                                     |
      | 30     | succeeds                                   |
      | 31     | fails with "tree exceeds max height"       |

  @FOG_GEFS_211
  Scenario Outline: Any sequence of upserts leaves a tree that agrees with a simple map
    Given an empty tree
    When it is given 1500 random upserts of up to 6 messages over 1500 keys from seed <seed>, checked every 250
    Then it has grown to 3 high
    And every key looks up as it was last given and a scan gives them in key order
    And every block of it is well formed, and every block it no longer uses was freed

    Examples:
      | seed |
      | 1    |
      | 2    |
      | 3    |

  @FOG_GEFS_212
  Scenario Outline: The tree takes the shape 9front's gefs gives it under the same upserts
    Given a tree whose root is a leaf holding only the key 10
    When the upserts of the <run> oracle run are applied
    Then after every tenth the tree has the shape gefs's own tree.c gave it

    Examples:
      | run                 |
      | three-high-and-back |
      | two-high-and-back   |
      | small-entries       |
