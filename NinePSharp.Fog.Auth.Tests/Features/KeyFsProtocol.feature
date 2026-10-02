@fog_keyfs_protocol
Feature: The keyfs tree speaks 9P2000 as keyfs.c does
  The admin socket serves the keyfs tree over 9P2000 with keyfs.c's messages and errors:
  version negotiation, per-connection fids, walks, qids, directory reads and stat. Each
  scenario's client has negotiated 9P2000 and attached its root fid 0.

  Background:
    Given a keyfs with the users "glenda" and "bootes", made in that order
    And a 9P client attached to its admin socket

  @FOG_KEYFS_P01
  Scenario Outline: Version negotiation clamps the message size and names the dialect
    When the client negotiates version "<offered>" with message size <offered size>
    Then the server answers version "<version>" with message size <size>

    Examples:
      | offered  | offered size | version | size |
      | 9P2000   | 65536        | 9P2000  | 8216 |
      | 9P2000.L | 8192         | 9P2000  | 8192 |
      | 9P       | 4096         | 9P2000  | 4096 |
      | XP2000   | 8192         | unknown | 8192 |

  @FOG_KEYFS_P01
  Scenario: A new version forgets the connection's fids
    When the client negotiates version "9P2000" with message size 8192
    Then reading the root through fid 0 fails with "read of unused fid"

  @FOG_KEYFS_P02
  Scenario Outline: Requests on a fid that is not in use are refused
    When the client sends <request> on fid 99, which is not in use
    Then it receives the error "<error>"

    Examples:
      | request | error                  |
      | walk    | walk of unused fid     |
      | open    | open of unused fid     |
      | create  | create of unused fid   |
      | read    | read of unused fid     |
      | write   | permission denied      |
      | clunk   | clunk of unused fid    |
      | remove  | permission denied      |
      | stat    | stat on unattached fid |
      | wstat   | permission denied      |

  @FOG_KEYFS_P02
  Scenario: Clunking a fid frees it
    Given fid 5 is walked to "glenda"
    When fid 5 is clunked
    Then the clunk succeeds and fid 5 is not in use

  @FOG_KEYFS_P03
  Scenario: Walking into a fid that is already in use is refused
    Given fid 5 is walked to "glenda"
    When fid 0 is walked to "bootes" into fid 5
    Then it receives the error "fid in use"
    And fid 5 still names "glenda"

  @FOG_KEYFS_P03
  Scenario Outline: Walks follow the two-level tree
    When fid 0 is walked through "<names>" into fid 7
    Then the walk returns <count> qids and fid 7 names "<node>"

    Examples:
      | names                | count | node   |
      |                      | 0     | keys   |
      | ..                   | 1     | keys   |
      | glenda/..            | 2     | keys   |
      | glenda/../bootes/key | 4     | key    |
      | glenda/key           | 2     | key    |

  @FOG_KEYFS_P03
  Scenario Outline: A walk that fails after its first name returns the qids it walked and no fid
    When fid 0 is walked through "<names>" into fid 7
    Then the walk returns <count> qids and fid 7 is not in use

    Examples:
      | names               | count |
      | glenda/nothere      | 1     |
      | glenda/key/deeper   | 2     |

  @FOG_KEYFS_P03
  Scenario Outline: A walk whose first name fails is refused
    When fid 0 is walked through "<names>" into fid 7
    Then it receives the error "<error>"

    Examples:
      | names    | error                   |
      | nobody   | file not found          |
      | key      | file not found          |

  @FOG_KEYFS_P03
  Scenario: Walking to an unknown file from a user directory is refused
    Given fid 5 is walked to "glenda"
    When fid 5 is walked through "nothere" into fid 7
    Then it receives the error "file not found"

  @FOG_KEYFS_P03
  Scenario: Walking below a file is refused
    Given fid 5 is walked to "glenda/key"
    When fid 5 is walked through "deeper" into fid 7
    Then it receives the error "file is not a directory"

  # NinePSharp's message validation enforces MAXWELEM before a request reaches keyfs.
  @FOG_KEYFS_P03
  Scenario Outline: A walk of more than 16 names is refused
    When fid 0 is walked through <count> ".." names into fid 7
    Then <outcome>

    Examples:
      | count | outcome                                           |
      | 16    | the walk returns 16 qids and fid 7 names "keys"   |
      | 17    | it receives the error "Validation error: Too many walk components" |

  @FOG_KEYFS_P04
  Scenario: Qids carry the node in their low byte and the user above it
    Then the qid of "" is a directory with path 0
    And the qid of "glenda" is a directory with path 1 plus 256 times its user number
    And the qid of "glenda/key" is a file with path 2 plus 256 times its user number
    And the qid of "glenda/warnings" is a file with path 9 plus 256 times its user number
    And "glenda" and "bootes" have different user numbers

  @FOG_KEYFS_P04
  Scenario: A renamed user keeps its qids
    When "glenda" is renamed to "glenda2" over 9P
    Then the qid of "glenda2" equals the qid "glenda" had

  @FOG_KEYFS_P05
  Scenario Outline: Opening reports the qid and an I/O unit of the message size less the header
    When "<path>" is opened for <mode>
    Then <outcome>

    Examples:
      | path       | mode    | outcome                                            |
      | glenda/key | reading | the open reports the qid of "glenda/key" and I/O unit 8192 |
      | glenda     | reading | the open reports the qid of "glenda" and I/O unit 8192     |
      | glenda     | writing | it receives the error "user already exists"        |
      | glenda     | truncation | it receives the error "user already exists"     |
      |            | reading | the open reports the qid of "" and I/O unit 8192   |

  @FOG_KEYFS_P05
  Scenario: Creating a user reports its directory's qid and I/O unit
    When the client creates the directory "scott" in the root
    Then the create reports the qid of "scott" and I/O unit 8192

  @FOG_KEYFS_P06
  Scenario: The root lists users in the order they were made
    Then the root lists "glenda, bootes"

  @FOG_KEYFS_P06
  Scenario: Directory reads return whole entries from an entry boundary
    When the root is read with a count one byte smaller than its first entry
    Then the read returns no entries
    When the root is read with a count exactly the size of its first entry
    Then the read returns exactly the entry "glenda"
    When the root is read from the offset just past its first entry
    Then the read returns exactly the entry "bootes"

  @FOG_KEYFS_P07
  Scenario Outline: Stat names each node with keyfs's owner and modes
    Then the stat of "<path>" is named "<name>" with mode <mode>, length 0 and owner "auth"

    Examples:
      | path            | name     | mode       |
      |                 | keys     | 0x800001FF |
      | glenda          | glenda   | 0x800001FF |
      | glenda/key      | key      | 0x000001B6 |
      | glenda/warnings | warnings | 0x000001B6 |

  @FOG_KEYFS_P08
  Scenario Outline: Renames are checked as user names
    When "<path>" is renamed to "<name>" over 9P
    Then it receives the error "<error>"

    Examples:
      | path       | name                         | error               |
      | glenda     |                              | bad user name       |
      | glenda     | abcdefghijklmnopqrstuvwxyz01 | bad user name       |
      | glenda     | has space                    | bad user name       |
      | glenda     | .                            | bad user name       |
      | glenda     | ..                           | bad user name       |
      | glenda     | bootes                       | user already exists |
      | glenda     | glenda                       | user already exists |
      | glenda/key | anything                     | permission denied   |
      |            | anything                     | permission denied   |

  @FOG_KEYFS_P08
  Scenario Outline: Making a user named "." or ".." is refused
    When the client creates the directory "<name>" in the root
    Then it receives the error "bad user name"

    Examples:
      | name |
      | .    |
      | ..   |

  # NinePSharp's parser refuses message types outside the negotiated dialect before keyfs sees
  # them; keyfs's own "bad fcall type" is covered by KeyFsDispatcherTests.
  @FOG_KEYFS_P09
  Scenario: Requests outside 9P2000 are refused with their own tag
    When a raw connection sends a 9P2000.L getattr with tag 7
    Then the raw connection receives the error "Unknown message type: 24" with tag 7

  @FOG_KEYFS_P09
  Scenario: Flush is answered
    When the client flushes tag 1
    Then the flush is answered

  @FOG_KEYFS_P10
  Scenario: Fids belong to their connection
    Given a second 9P client attached to the admin socket
    When the first client walks fid 0 to "glenda" into fid 5
    Then the second client's fid 5 is not in use

  @FOG_KEYFS_P10
  Scenario: A closed connection's fids are forgotten
    When 20 more clients attach and disconnect
    Then the keyfs holds fids for only the open connection
    And the admin listener tracks only the open connection
