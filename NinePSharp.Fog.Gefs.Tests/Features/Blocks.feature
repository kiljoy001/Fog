@fog_gefs
Feature: Blocks are laid out, searched, sealed and read as 9front's gefs does
  Every gefs block is 16 KiB. A tree block starts with its type and counts: a leaf with how many
  values it holds and their bytes, a pivot also with how many messages it buffers and their bytes.
  An offset table grows from the start of the block's space and the values it points at are packed
  from the end. A pivot keeps child pointers in its first half and buffered messages in its second.
  Searching finds the first entry at a key, or the last one before it. Sealing a block writes its
  header and hashes all 16 KiB; reading checks that hash, or for an allocation or deadlist log,
  the hash of the log bytes in use. Fog's hash is SHA-256, so a log's header is longer than gefs's.

  @FOG_GEFS_101
  Scenario: A leaf keeps its offsets at the start and its values packed from the end
    Given a new leaf
    When it is given the value 6161 at key 01 and then the value 62 at key 0203
    Then it holds 2 values in 14 bytes
    And its first offset points 7 bytes before the end of its space, and its second 14
    And its values read back in the order they were given

  @FOG_GEFS_102
  Scenario Outline: Searching a block finds the first entry at a key, or the last before it
    Given a new leaf holding keys 10, 20, 20 and 30
    When it is searched for <key>
    Then the search lands on <index> and <whether> the key

    Examples:
      | key | index | whether   |
      | 05  | -1    | misses    |
      | 10  | 0     | finds     |
      | 15  | 0     | misses    |
      | 20  | 1     | finds     |
      | 25  | 2     | misses    |
      | 30  | 3     | finds     |
      | 40  | 3     | misses    |

  @FOG_GEFS_103
  Scenario: A pivot keeps child pointers in its first half and buffered messages in its second
    Given a new pivot
    When it points key 10 at block 49152 of generation 7 holding 300 bytes
    And it buffers an insert for key 20 of value 6a and then a delete for key 20
    Then it holds 1 pointer and 2 messages
    And the pointer reads back as block 49152 of generation 7 holding 300 bytes
    And the messages read back in the order they were buffered, the first packed 7 bytes before the end of its second half

  @FOG_GEFS_103
  Scenario Outline: Searching a pivot's buffer finds the first of its messages at a key
    Given a new pivot buffering messages for keys 10, 20, 20 and 30
    When its buffer is searched for <key>
    Then the search lands on <index> and <whether> the key

    Examples:
      | key | index | whether |
      | 05  | -1    | misses  |
      | 20  | 1     | finds   |
      | 25  | 2     | misses  |

  @FOG_GEFS_104
  Scenario Outline: A leaf is full once a value of the needed size would not fit
    Given a new leaf filled to <used> bytes of its space with <count> values
    Then it <answer> full for a value needing <needed> bytes

    Examples:
      | used  | count | needed | answer |
      | 16000 | 100   | 176    | is not |
      | 16000 | 100   | 177    | is     |

  @FOG_GEFS_104
  Scenario Outline: A pivot keeps room for the pointers a split may bring up, and its buffer has its own room
    Given a new pivot filled to <used> bytes of pointer space with <count> pointers
    Then its pointers <answer> full when <reserve> pointers must still fit
    And its fill counts <fill> bytes

    Examples:
      | used | count | reserve | answer  | fill |
      | 7229 | 10    | 3       | are not | 7249 |
      | 7230 | 10    | 3       | are     | 7250 |
      | 7230 | 10    | 2       | are not | 7250 |

  @FOG_GEFS_104
  Scenario Outline: A pivot's buffer is full once messages of the needed size would not fit
    Given a new pivot buffering <count> messages in <used> bytes
    Then its buffer <answer> full for <more> more messages needing <needed> bytes
    And its fill counts 8100 bytes

    Examples:
      | count | used | more | needed | answer |
      | 100   | 7900 | 1    | 85     | is not |
      | 100   | 7900 | 1    | 86     | is     |
      | 100   | 7900 | 2    | 83     | is not |
      | 100   | 7900 | 2    | 84     | is     |

  @FOG_GEFS_104
  Scenario Outline: A block takes an entry that exactly fills its room, and refuses one a byte larger
    Given <block>
    When it is given a <entry> of <size> bytes, its key included
    Then it <result>

    Examples:
      | block                                                         | entry   | size | result                  |
      | a new leaf filled to 16000 bytes of its space with 100 values | value   | 176  | takes it                |
      | a new leaf filled to 16000 bytes of its space with 100 values | value   | 177  | fails with "block full" |
      | a new pivot buffering 100 messages in 7900 bytes              | message | 85   | takes it                |
      | a new pivot buffering 100 messages in 7900 bytes              | message | 86   | fails with "block full" |

  @FOG_GEFS_104
  Scenario Outline: A block refuses an entry it has no room for
    Given a new <kind> with no room left
    When it is given one more <entry>
    Then it fails with "block full"

    Examples:
      | kind  | entry   |
      | leaf  | value   |
      | pivot | pointer |
      | pivot | message |

  @FOG_GEFS_105
  Scenario Outline: A sealed tree block reads back as it was sealed
    Given a new <kind> holding entries
    When it is sealed at address 65536 in generation 3
    Then its hash is the SHA-256 of all 16 KiB of it
    And reading its bytes with its pointer gives a <kind> with the same counts and entries

    Examples:
      | kind  |
      | leaf  |
      | pivot |

  @FOG_GEFS_105
  Scenario Outline: Reading a block refuses one that does not match its pointer
    Given a new leaf holding entries
    When it is sealed at address 65536 in generation 3
    And its bytes are read with <change>
    Then reading fails with "<error>"

    Examples:
      | change                                | error                      |
      | byte 100 changed                      | block contents corrupted   |
      | another block's hash                  | block contents corrupted   |
      | its type changed to 9                 | invalid block type 9       |

  @FOG_GEFS_105
  Scenario: A read without checking takes a block's bytes as they are
    Given a new leaf holding entries
    When it is sealed at address 65536 in generation 3
    And its bytes are read with byte 100 changed, without checking
    Then reading gives a leaf

  @FOG_GEFS_105
  Scenario Outline: A raw read takes any block as data, but still checks its hash
    Given a new leaf holding entries
    When it is sealed at address 65536 in generation 3
    And its bytes are read as raw data <change>
    Then reading <result>

    Examples:
      | change            | result                                   |
      | as they are       | gives a data block                       |
      | with byte 100 changed | fails with "block contents corrupted" |

  @FOG_GEFS_106
  Scenario Outline: A log block hashes only the log bytes it uses, and chains to the next
    Given a new <kind> log chained to block 81920 of generation 4
    When it is given 40 bytes of log and sealed at address 98304 in generation 5
    Then its log hash is the SHA-256 of those 40 bytes
    And reading its bytes gives a <kind> log of 40 bytes chained to block 81920 of generation 4
    And reading it with a changed byte past its 40 bytes still succeeds, but with a changed byte among them fails with "block contents corrupted"

    Examples:
      | kind       |
      | allocation |
      | deadlist   |

  @FOG_GEFS_106
  Scenario: A log may use all of its block's space after its header, and no more
    Given a new allocation log chained to block 81920 of generation 4
    When it is given 16300 bytes of log and sealed at address 98304 in generation 5
    Then reading its bytes gives an allocation log of 16300 bytes chained to block 81920 of generation 4
    And reading it with its header claiming 16301 bytes fails with "block contents corrupted"

  @FOG_GEFS_107
  Scenario: A copied block holds the same entries at its new address, unsealed
    Given a new leaf holding entries
    When it is sealed at address 65536 in generation 3
    And it is copied to address 131072 in generation 6
    Then the copy holds the same counts and entries
    And the copy is at address 131072 in generation 6 and has no hash until it is sealed
