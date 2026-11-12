@fog_gefs_conn
Feature: A store is served over 9P, as gefs's fs.c serves a connection
  Each message is read whole and answered in turn. Tversion comes first: 9P2000, its minor version
  dropped, is answered with a message size of at most 32792; any other version is unknown. An
  attach names a user the table has, and is authenticated through an auth file, whose reads and
  writes are factotum's conversation with the client, as gefs runs without -A; none may attach
  once another attach on the connection has authenticated. An attach to a name starting with %
  has the permit, given only to a member of adm. A walk goes as far as the fid may search; a stat
  gives type and device ~0 and times rounded to the nearest second. A directory is read in
  stats, from its start at offset 0, and on from where the last read stopped otherwise. Where
  gefs would read or write a fid not opened for it, or fault on an auth file used as a file,
  Fog refuses with gefs's errors. A message gefs cannot read, one before Tversion, one too large
  and a reply that cannot be written hang the connection up; when it ends, every fid is clunked.

  In the messages, a fid of -1 is NOFID, modes are in octal, and {n} is a name of n bytes. The
  scripted factotum asks "proof?" until the client writes a user's name, which it then proves;
  "-" proves no one, "garbage" is refused and "confused" leaves it in error.

  Background:
    Given a device of 800 blocks reamed with 2 arenas by adm at time 100, served by a server
    And adm attaches to "main"
    And a client connected to the server

  @FOG_GEFS_711
  Scenario: Tversion gives 9P2000 without its minor version, at most 32792 bytes a message
    When the client sends:
      | message                   | reply                   |
      | Tversion 8216 9P2000      | Rversion 8216 9P2000    |
      | Tversion 99999 9P2000.L   | Rversion 32792 9P2000   |
      | Tversion 32792 9P2000     | Rversion 32792 9P2000   |
      | Tversion 8216 9P1         | Rversion 32792 unknown  |
    And the client sends "Tattach 1 -1 none main" without waiting
    Then the server hangs up with "version required"

  @FOG_GEFS_711
  Scenario Outline: The server hangs up on what it cannot take
    Given the client has versioned the connection with a message size of 8216
    When the client sends a message of <size> bytes of type <type>
    Then the server hangs up with "<why>"

    Examples:
      | size  | type | why                                  |
      | 7     | 255  | invalid message                      |
      | 7     | 101  | invalid message                      |
      | 8217  | 100  | read message: message size too large |
      | 6     | 100  | read message: message size too large |
      | 11    | 110  | invalid message                      |

  @FOG_GEFS_711
  Scenario: A message the client sends no Tversion before hangs up the connection
    When the client sends "Tattach 1 -1 none main" without waiting
    Then the server hangs up with "version required"

  @FOG_GEFS_711
  Scenario Outline: A connection that ends within a message is hung up
    When the client sends <sent> bytes of a message of 19 and hangs up
    Then the server hangs up with "read message: short read"

    Examples:
      | sent |
      | 3    |
      | 10   |

  @FOG_GEFS_711
  Scenario: A reply the client cannot hear hangs the connection up
    Given the client has versioned the connection with a message size of 8216
    When the client stops hearing replies
    And the client sends "Tflush 1" without waiting
    Then the server hangs up with "i/o error"

  @FOG_GEFS_711
  Scenario Outline: The first message of a connection may be as large as gefs's own message size
    When the client sends a message of <size> bytes of type 255
    Then the server hangs up with "<why>"

    Examples:
      | size  | why                                  |
      | 32768 | invalid message                      |
      | 32769 | read message: message size too large |

  @FOG_GEFS_712
  Scenario: An attach is authenticated through an auth file factotum speaks for
    Given the client has versioned the connection with a message size of 8216
    When the client sends:
      | message                    | reply                                          |
      | Tauth 100 glenda main      | Rauth auth                                     |
      | Tread 100 0 6              | Rread proof?                                   |
      | Tread 100 0 100            | Rread proof?                                   |
      | Twrite 100 0 glenda        | Rwrite 6                                       |
      | Tattach 1 100 glenda main  | Rattach dir                                    |
      | Tstat 1                    | Rstat '' adm adm adm 775 0 100 100 ffff ffffffff |
      | Tattach 2 -1 none main     | Rattach dir                                    |
      | Tattach 3 100 glenda main  | Rerror auth phase error                        |
      | Tclunk 100                 | Rclunk                                         |
      | Tclunk 100                 | Rerror unknown fid                             |
      | Tflush 1                   | Rflush                                         |
    Then 1 authentication was started and each ended once

  @FOG_GEFS_712
  Scenario Outline: An attach the server cannot authenticate is refused
    Given the client has versioned the connection with a message size of 8216
    When the client sends:
      | message                     | reply                        |
      | Tauth 100 glenda main       | Rauth auth                   |
      | <write>                     | <written>                    |
      | Tattach 1 <afid> <user> <aname> | Rerror <error>           |
      | Tstat 1                     | Rerror unknown fid           |

    Examples:
      | write                 | written                  | afid | user     | aname   | error                                            |
      | Twrite 100 0 glenda   | Rwrite 6                 | -1   | glenda   | main    | attach -- unknown user or failed authentication  |
      | Twrite 100 0 glenda   | Rwrite 6                 | -1   | none     | main    | attach -- unknown user or failed authentication  |
      | Twrite 100 0 glenda   | Rwrite 6                 | 100  | bob      | main    | attach -- unknown user or failed authentication  |
      | Twrite 100 0 glenda   | Rwrite 6                 | 7    | glenda   | main    | unknown fid                                      |
      | Twrite 100 0 glenda   | Rwrite 6                 | 100  | glenda   | tuesday | attach -- bad specifier                          |
      | Twrite 100 0 glenda   | Rwrite 6                 | 100  | stranger | main    | user does not exist                              |
      | Twrite 100 0 glenda   | Rwrite 6                 | 100  | glenda   | %main   | permission denied                                |
      | Tflush 1              | Rflush                   | 100  | glenda   | main    | authread -- not enough data                      |
      | Twrite 100 0 -        | Rwrite 1                 | 100  | glenda   | main    | auth phase error                                 |
      | Twrite 100 0 stranger | Rwrite 8                 | 100  | glenda   | main    | user does not exist                              |
      | Twrite 100 0 confused | Rwrite 8                 | 100  | glenda   | main    | authread -- auth protocol not finished           |
      | Twrite 100 0 garbage  | Rerror protocol botch    | 100  | glenda   | main    | authread -- not enough data                      |

  @FOG_GEFS_712
  Scenario: An auth file is not a file
    Given the client has versioned the connection with a message size of 8216
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                   | reply                                 |
      | Tauth 100 glenda main     | Rauth auth                            |
      | Tauth 100 glenda main     | Rerror fid in use                     |
      | Tread 100 0 2             | Rerror authread -- not enough data    |
      | Tstat 100                 | Rerror permission denied              |
      | Twstat 100 mode 0600      | Rerror open/create -- unknown mode    |
      | Twalk 100 101             | Rerror invalid fid type               |
      | Topen 100 0               | Rerror invalid fid type               |
      | Tcreate 100 f 0644 1      | Rerror invalid fid type               |
      | Tattach 2 1 glenda main   | Rerror invalid fid type               |
      | Tremove 100               | Rerror invalid fid type               |
      | Tread 100 0 100           | Rerror unknown fid                    |
      | Tauth 100 none main       | Rerror auth -- user 'none' requires no authentication |
    Then 3 authentications were started and each ended once

  @FOG_GEFS_712
  Scenario: Without factotum there is no auth file
    Given factotum cannot be reached, failing with "can't open /mnt/factotum/rpc"
    And the client has versioned the connection with a message size of 8216
    When the client sends:
      | message                | reply                                 |
      | Tauth 100 glenda main  | Rerror can't open /mnt/factotum/rpc   |
      | Tread 100 0 100        | Rerror unknown fid                    |

  @FOG_GEFS_712
  Scenario: A member of adm attached to %main has the permit
    Given the client has versioned the connection with a message size of 8216
    And adm creates file notes in the root with mode 0600 at time 200
    And the client has attached fid 1 as adm to "%main"
    And the client has attached fid 2 as adm to "main"
    When the client sends:
      | message                 | reply                          |
      | Twalk 2 3 notes         | Rwalk file                     |
      | Twstat 3 owner glenda   | Rerror wstat -- not owner      |
      | Twalk 1 4 notes         | Rwalk file                     |
      | Twstat 4 owner glenda   | Rwstat                         |
      | Tstat 3                 | Rstat notes glenda adm adm 600 0 200 200 ffff ffffffff |
      | Tattach 2 -1 none main  | Rerror fid in use              |
      | Twstat 9 mode 0600      | Rerror unknown fid             |

  @FOG_GEFS_713
  Scenario: A walk goes as far as the fid may search
    Given the client has versioned the connection with a message size of 8216
    And adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file notes in usr with mode 0644 at time 200
    And adm creates directory private in the root with mode d0770 at time 200
    And adm creates file x in private with mode 0644 at time 200
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                  | reply                                 |
      | Twalk 1 2 usr notes      | Rwalk dir file                        |
      | Twalk 1 3 usr nonesuch   | Rwalk dir                             |
      | Tstat 3                  | Rerror unknown fid                    |
      | Twalk 1 3 private x      | Rwalk dir                             |
      | Tstat 3                  | Rerror unknown fid                    |
      | Twalk 1 3 nonesuch       | Rerror directory entry not found      |
      | Twalk 2 3 x              | Rerror directory entry not found      |
      | Twalk 1 3 {244}          | Rerror directory entry not found      |
      | Twalk 1 3 {245}          | Rerror name too long                  |
      | Twalk 1 2 usr            | Rerror fid in use                     |
      | Twalk 9 10 usr           | Rerror unknown fid                    |
      | Twalk 1 4                | Rwalk                                 |
      | Twalk 4 4 usr            | Rwalk dir                             |
      | Tstat 4                  | Rstat usr adm adm adm 775 0 200 200 ffff ffffffff |
      | Twalk 4 5 ..             | Rwalk dir                             |
      | Tstat 5                  | Rstat '' adm adm adm 775 0 100 100 ffff ffffffff |
      | Topen 2 0                | Ropen file 8216                       |
      | Twalk 2 6                | Rerror resource in use                |

  @FOG_GEFS_714
  Scenario: Files are opened, made, read and written as the fid may
    Given the client has versioned the connection with a message size of 8216
    And adm creates directory tmp in the root with mode d0777 at time 200
    And adm sets tmp's mode to d0777, group to adm and owner to adm at time 200
    And adm creates file notes in the root with mode 0644 at time 200
    And adm writes "hello" to notes at offset 0 at time 200
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                  | reply                              |
      | Twalk 1 2 notes          | Rwalk file                         |
      | Tread 2 0 10             | Rerror resource in use             |
      | Twrite 2 0 x             | Rerror resource in use             |
      | Topen 2 1                | Rerror permission denied           |
      | Topen 2 0                | Ropen file 8216                    |
      | Topen 2 0                | Rerror resource in use             |
      | Tread 2 0 10             | Rread hello                        |
      | Tread 2 3 10             | Rread lo                           |
      | Twrite 2 0 x             | Rerror resource in use             |
      | Tcreate 2 f 0644 1       | Rerror resource in use             |
      | Tcreate 1 f 0644 1       | Rerror permission denied           |
      | Twalk 1 3 tmp            | Rwalk dir                          |
      | Topen 3 1                | Rerror permission denied           |
      | Tcreate 3 f 0644 1       | Rcreate file 8216                  |
      | Twrite 3 0 made          | Rwrite 4                           |
      | Tread 3 0 10             | Rerror resource in use             |
      | Tstat 3                  | Rstat f glenda adm glenda 644 4 200 200 ffff ffffffff |
      | Tclunk 3                 | Rclunk                             |
      | Twalk 1 4 tmp f          | Rwalk dir file                     |
      | Topen 4 22               | Ropen file 8216                    |
      | Tread 4 0 10             | Rread                              |
      | Tcreate 9 f 0644 1       | Rerror unknown fid                 |
      | Topen 9 0                | Rerror unknown fid                 |
      | Tread 9 0 1              | Rerror unknown fid                 |
      | Twrite 9 0 x             | Rerror unknown fid                 |
      | Tstat 9                  | Rerror unknown fid                 |

  @FOG_GEFS_714
  Scenario: A file opened to execute may be read, as gefs gives an open to execute the bit to read
    Given the client has versioned the connection with a message size of 8216
    And adm creates file run in the root with mode 0755 at time 200
    And adm writes "hello" to run at offset 0 at time 200
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                  | reply                              |
      | Twalk 1 2 run            | Rwalk file                         |
      | Topen 2 3                | Ropen file 8216                    |
      | Tread 2 0 10             | Rread hello                        |

  @FOG_GEFS_714
  Scenario: A file opened or made to be removed on close goes when its fid is clunked, or removed, a directory even with files in it as gefs removes one
    Given the client has versioned the connection with a message size of 8216
    And adm creates directory tmp in the root with mode d0777 at time 200
    And adm sets tmp's mode to d0777, group to adm and owner to adm at time 200
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                  | reply                               |
      | Twalk 1 2 tmp            | Rwalk dir                           |
      | Tcreate 2 gone 0644 101  | Rcreate file 8216                   |
      | Tclunk 2                 | Rclunk                              |
      | Twalk 1 3 tmp gone       | Rwalk dir                           |
      | Twalk 1 2 tmp            | Rwalk dir                           |
      | Tcreate 2 also 0644 101  | Rcreate file 8216                   |
      | Tremove 2                | Rremove                             |
      | Twalk 1 3 tmp also       | Rwalk dir                           |
      | Twalk 1 2 tmp            | Rwalk dir                           |
      | Tcreate 2 d d0777 100    | Rcreate dir 8216                    |
      | Twalk 1 3 tmp d          | Rwalk dir dir                       |
      | Tcreate 3 inside 0644 1  | Rcreate file 8216                   |
      | Tclunk 2                 | Rclunk                              |
      | Twalk 1 4 tmp d inside   | Rwalk dir                           |

  @FOG_GEFS_714
  Scenario: A fid removed is clunked, whether or not the file could be removed
    Given the client has versioned the connection with a message size of 8216
    And adm creates directory tmp in the root with mode d0777 at time 200
    And adm sets tmp's mode to d0777, group to adm and owner to adm at time 200
    And adm creates file notes in the root with mode 0644 at time 200
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                  | reply                               |
      | Twalk 1 2 notes          | Rwalk file                          |
      | Tremove 2                | Rerror permission denied            |
      | Tstat 2                  | Rerror unknown fid                  |
      | Twalk 1 2 tmp            | Rwalk dir                           |
      | Tcreate 2 f 0644 1       | Rcreate file 8216                   |
      | Tremove 2                | Rremove                             |
      | Twalk 1 2 tmp f          | Rwalk dir                           |
      | Tremove 9                | Rerror unknown fid                  |

  @FOG_GEFS_714
  Scenario: A tree served from a snapshot is read only, and a fid removed from it is clunked
    Given the client has versioned the connection with a message size of 8216
    And adm creates file notes in the root with mode 0666 at time 200
    And the server snapshots main as monday
    And the client has attached fid 1 as adm to "monday"
    When the client sends:
      | message                  | reply                              |
      | Twalk 1 2 notes          | Rwalk file                         |
      | Tremove 2                | Rerror file system read only       |
      | Tstat 2                  | Rerror unknown fid                 |
      | Twalk 1 2 notes          | Rwalk file                         |
      | Topen 2 100              | Ropen file 8216                    |
      | Tclunk 2                 | Rclunk                             |
      | Twalk 1 2 notes          | Rwalk file                         |

  @FOG_GEFS_715
  Scenario: A stat rounds its times to the nearest second
    Given the client has versioned the connection with a message size of 8216
    And adm creates directory tmp in the root with mode d0777 at time 200
    And adm sets tmp's mode to d0777, group to adm and owner to adm at time 200
    And the client has attached fid 1 as glenda to "main"
    When the clock reads 300.4 seconds
    Then the client sends:
      | message                  | reply                              |
      | Twalk 1 2 tmp            | Rwalk dir                          |
      | Tcreate 2 early 0644 1   | Rcreate file 8216                  |
    When the clock reads 300.5 seconds
    Then the client sends:
      | message                  | reply                              |
      | Twalk 1 3 tmp            | Rwalk dir                          |
      | Tcreate 3 late 0644 1    | Rcreate file 8216                  |
      | Twalk 1 4 tmp            | Rwalk dir                          |
      | Topen 4 0                | Ropen dir 8216                     |
    And reading fid 4 at offset 0 with a count of 8000 gives the stats:
      | stat                                                    |
      | late glenda adm glenda 644 0 301 301 ffff ffffffff     |
      | early glenda adm glenda 644 0 300 300 ffff ffffffff    |

  @FOG_GEFS_715
  Scenario: A stat gives the file's qid, its version counting the file's changes
    Given the client has versioned the connection with a message size of 8216
    And adm creates file notes in the root with mode 0644 at time 200
    And adm writes "hello" to notes at offset 0 at time 200
    And the client has attached fid 1 as glenda to "main"
    Then a walk from fid 1 to notes and a stat of it give the same qid, of version 1

  @FOG_GEFS_715
  Scenario: A directory is read from its start at offset 0, and on from where the last read stopped
    Given the client has versioned the connection with a message size of 8216
    And adm creates directory tmp in the root with mode d0777 at time 200
    And adm sets tmp's mode to d0777, group to adm and owner to adm at time 200
    And adm creates file a in tmp with mode 0644 at time 200
    And adm creates file bb in tmp with mode 0644 at time 200
    And adm creates file ccc in tmp with mode 0644 at time 200
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                  | reply                              |
      | Twalk 1 2 tmp            | Rwalk dir                          |
      | Topen 2 0                | Ropen dir 8216                     |
    Then reading fid 2 at offset 0 with a count of 8000 lists a bb ccc
    And reading fid 2 at offset 180 with a count of 8000 lists nothing
    And reading fid 2 at offset 0 with a count of 119 lists a bb
    And reading fid 2 at offset 119 with a count of 119 lists ccc
    And reading fid 2 at offset 180 with a count of 119 lists nothing
    And reading fid 2 at offset 0 with a count of 59 lists a
    And reading fid 2 at offset 59 with a count of 60 lists bb
    And reading fid 2 at offset 119 with a count of 61 lists ccc
    And reading fid 2 at offset 180 with a count of 61 lists nothing

  @FOG_GEFS_715
  Scenario: A directory read with no room for the next entry twice is a protocol botch
    Given the client has versioned the connection with a message size of 8216
    And adm creates directory tmp in the root with mode d0777 at time 200
    And adm sets tmp's mode to d0777, group to adm and owner to adm at time 200
    And adm creates file a in tmp with mode 0644 at time 200
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                  | reply                              |
      | Twalk 1 2 tmp            | Rwalk dir                          |
      | Topen 2 0                | Ropen dir 8216                     |
      | Tread 2 0 10             | Rread                              |
      | Tread 2 1 10             | Rerror protocol botch              |
    Then reading fid 2 at offset 0 with a count of 8000 lists a

  @FOG_GEFS_715
  Scenario: A scan that has ended stays ended until it starts again at offset 0
    Given the client has versioned the connection with a message size of 8216
    And adm creates directory tmp in the root with mode d0777 at time 200
    And adm sets tmp's mode to d0777, group to adm and owner to adm at time 200
    And adm creates file a in tmp with mode 0644 at time 200
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                  | reply                              |
      | Twalk 1 2 tmp            | Rwalk dir                          |
      | Topen 2 0                | Ropen dir 8216                     |
    Then reading fid 2 at offset 0 with a count of 8000 lists a
    When adm creates file b in tmp with mode 0644 at time 300
    Then reading fid 2 at offset 59 with a count of 8000 lists nothing
    And reading fid 2 at offset 0 with a count of 8000 lists a b

  @FOG_GEFS_716
  Scenario: When the client hangs up every fid is clunked, its files removed on close and its conversations ended
    Given the client has versioned the connection with a message size of 8216
    And adm creates directory tmp in the root with mode d0777 at time 200
    And adm sets tmp's mode to d0777, group to adm and owner to adm at time 200
    And the client has attached fid 1 as glenda to "main"
    When the client sends:
      | message                  | reply                              |
      | Twalk 1 2 tmp            | Rwalk dir                          |
      | Tcreate 2 gone 0644 101  | Rcreate file 8216                  |
      | Tauth 100 glenda main    | Rauth auth                         |
    And the client hangs up
    Then the server ends the connection without hanging up
    And 2 authentications were started and each ended once
    And tmp lists nothing
