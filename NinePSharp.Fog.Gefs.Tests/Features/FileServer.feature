@fog_gefs
Feature: A mounted tree serves files and directories, after 9front's gefs
  A directory entry is kept under its parent's qid and its name, its stat in the entry's value.
  Unlike gefs, which looks files up through state it keeps for each open fid, every entry also
  keeps, under its own qid, the key of its directory entry, so a handle alone finds it. A file's
  data lives in 16 KiB blocks under its qid and offset; a hole reads as zeros. Creating needs
  write permission on the directory, and takes the directory's group and as much of the
  requested mode as the directory's own allows. Removing needs write permission on the directory,
  and a directory must be empty. Opening needs the permissions its mode asks for; a directory
  opens only to read, and opening with truncation empties a file. Writing replaces the blocks it
  touches and sets the file's length, modification time and last modifier. A wstat changes only
  what it names, after every check has passed; one that names nothing commits the store. Each
  change bumps the qid version of what it changes, and creating, removing or renaming an entry
  bumps its directory's.

  Users are each in a group of their own name; bob is in dev too and glenda in sys. As in gefs
  for a group with no leader named, any member of a group may act as its leader. Members of adm
  may give files away. Times are in seconds. Paths are walked from the root, and
  names list in key order: shorter names first.

  Background:
    Given a device of 800 blocks reamed with 2 arenas by adm at time 100, serving main

  @FOG_GEFS_601
  Scenario: A reamed store's main is a directory owned by the user that reamed it
    Then the root is a directory "/" owned by adm in group adm, mode d0775, version 0, modified at 100
    And walking .. from the root gives the root
    And the root lists nothing

  @FOG_GEFS_602
  Scenario: Creating takes the creator as owner and the directory's group
    When adm sets the root's mode to d0777 at time 150
    And glenda creates directory glenda in the root with mode d0777 at time 300
    And glenda creates file profile in glenda with mode 0666 at time 400
    Then glenda is a directory "glenda" owned by glenda in group adm, mode d0777, version 1, modified at 300
    And glenda/profile is a file "profile" owned by glenda in group adm, mode 0666, version 0, modified at 400
    And the root is a directory "/" owned by adm in group adm, mode d0777, version 2, modified at 100
    And the root lists glenda
    And glenda lists profile
    And walking .. from glenda gives the root

  @FOG_GEFS_602
  Scenario Outline: An entry created for no user belongs to the tree's owner, with all the permission its directory gives
    When the server creates <kind> x in the root
    Then x is a <kind> "x" owned by adm in group adm, mode <got>, version 0, modified at 100

    Examples:
      | kind      | got   |
      | file      | 0664  |
      | directory | d0775 |

  @FOG_GEFS_602
  Scenario Outline: A created entry has no more permission than its directory gives
    When adm creates directory lib in the root with mode <dir> at time 200
    And adm creates <kind> x in lib with mode <asked> at time 300
    Then lib/x is a <kind> "x" owned by adm in group adm, mode <got>, version 0, modified at 300

    Examples:
      | dir   | kind      | asked | got   |
      | d0750 | file      | 0666  | 0640  |
      | d0750 | directory | d0777 | d0750 |
      | d0700 | file      | 0644  | 0600  |
      | d0777 | file      | 0666  | 0664  |
      | d0750 | file      | 0777  | 0751  |

  @FOG_GEFS_603
  Scenario: A directory lists its entries shorter names first
    When adm creates files zz, a, bbb and c in the root with mode 0644 at time 200
    Then the root lists a, c, zz and bbb

  @FOG_GEFS_604
  Scenario Outline: Creating refuses what gefs refuses
    When adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file notes in the root with mode 0644 at time 200
    Then <who> creating <what> fails with "<error>"

    Examples:
      | who    | what                                 | error                                      |
      | glenda | file x in the root                   | permission denied                          |
      | adm    | file usr in the root                 | create/wstat -- file exists                |
      | adm    | file x in notes                      | create -- in a non-directory               |
      | adm    | file a/b in the root                 | create/wstat -- bad character in file name |
      | adm    | file . in the root                   | create/wstat -- bad character in file name |
      | adm    | file .. in the root                  | create/wstat -- bad character in file name |
      | adm    | a file with no name in the root      | create/wstat -- bad character in file name |
      | adm    | a file named with a tab in the root  | create/wstat -- bad character in file name |
      | adm    | a file named in 245 bytes in the root | name too long                             |
      | adm    | a mount point x in the root          | protocol botch                             |
      | adm    | an authentication file x in the root | protocol botch                             |
      | adm    | a file x with unknown mode bits      | wstat -- unknown bits in qid.type/mode     |

  @FOG_GEFS_604
  Scenario: A name may be as long as a directory entry allows
    When adm creates a file named in 244 bytes in the root
    Then the root lists the name of 244 bytes

  @FOG_GEFS_604
  Scenario: A name may hold spaces
    When adm creates a file named with a space in the root
    Then the root lists a b

  @FOG_GEFS_605
  Scenario: Removing a file takes it out of its directory, and frees its data once committed
    When adm creates file notes in the root with mode 0644 at time 200
    And adm writes 40000 bytes to notes at offset 0 at time 300
    And the store commits
    And adm removes notes
    And the store commits
    Then the root lists nothing
    And walking notes from the root finds nothing
    And the root is a directory "/" owned by adm in group adm, mode d0775, version 2, modified at 100
    And every block of the device is held, in a log or free
    When adm creates file notes in the root with mode 0644 at time 400
    Then statting the removed notes fails with "phase error -- use after remove"

  @FOG_GEFS_605
  Scenario: Removing an empty directory leaves the entries beside it
    When adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file notes in the root with mode 0644 at time 200
    And adm removes usr
    Then the root lists notes

  @FOG_GEFS_605
  Scenario Outline: Removing refuses what gefs refuses
    When adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file profile in usr with mode 0666 at time 200
    Then <who> removing <what> fails with "<error>"

    Examples:
      | who    | what        | error                  |
      | adm    | usr         | directory is not empty |
      | glenda | usr/profile | permission denied      |

  @FOG_GEFS_605
  Scenario: The root cannot be removed
    Then adm removing the root fails with "permission denied"

  @FOG_GEFS_606
  Scenario: A write is read back, and sets the length, modification time and last modifier
    When adm creates file notes in the root with mode 0666 at time 200
    And adm writes "hello, world" to notes at offset 0 at time 300
    And adm creates file other in the root with mode 0666 at time 300
    And adm writes "other" to other at offset 0 at time 300
    Then reading 100 bytes of notes at offset 0 gives "hello, world"
    And reading 100 bytes of notes at offset 7 gives "world"
    And reading 100 bytes of notes at offset 12 gives nothing
    And reading 100 bytes of notes at offset 50 gives nothing
    And reading 100 bytes of other at offset 0 gives "other"
    And notes is a file "notes" owned by adm in group adm, mode 0664, version 1, modified at 300, 12 bytes long, last changed by adm

  @FOG_GEFS_606
  Scenario: Writes across blocks and into the middle of a block keep the bytes around them, and holes read as zeros
    When adm creates file data in the root with mode 0644 at time 200
    And adm writes "abcdefghij" to data at offset 16380 at time 300
    And adm writes "XY" to data at offset 16382 at time 400
    Then reading 10 bytes of data at offset 16380 gives "abXYefghij"
    And reading 4 bytes of data at offset 0 gives 4 zeros
    And reading 4 bytes of data at offset 16376 gives 4 zeros
    And data is a file "data" owned by adm in group adm, mode 0644, version 2, modified at 400, 16390 bytes long, last changed by adm

  @FOG_GEFS_606
  Scenario: A write may span many blocks
    When adm creates file big in the root with mode 0644 at time 200
    And adm writes 2000000 bytes to big at offset 5 at time 300
    And the store commits
    Then reading the 2000000 bytes of big at offset 5 gives what was written
    And every block of the device is held, in a log or free

  @FOG_GEFS_606
  Scenario: An append-only file is written at its end
    When adm creates an append-only file log in the root with mode 0644 at time 200
    And adm writes "one" to log at offset 0 at time 300
    And adm writes "two" to log at offset 0 at time 400
    Then reading 100 bytes of log at offset 0 gives "onetwo"

  @FOG_GEFS_606
  Scenario: Writing needs a file opened for writing
    When adm creates file notes in the root with mode 0644 at time 200
    Then adm writing to notes opened only for reading fails with "resource in use"

  @FOG_GEFS_607
  Scenario: Opening with truncation empties a file and frees its blocks once committed
    When adm creates file notes in the root with mode 0644 at time 200
    And adm writes 40000 bytes to notes at offset 0 at time 300
    And the store commits
    And adm opens notes with truncation
    And the store commits
    Then notes is a file "notes" owned by adm in group adm, mode 0644, version 2, modified at 300, 0 bytes long, last changed by adm
    And reading 10 bytes of notes at offset 0 gives nothing
    And every block of the device is held, in a log or free

  @FOG_GEFS_607
  Scenario Outline: Opening needs the permission its mode asks for
    When adm creates file plan in the root with mode 0600 at time 200
    And adm sets plan's mode to <mode>, group to <group> and owner to <owner> at time 200
    Then <who> opening plan to <how> <result>

    Examples:
      | mode | owner | group | who    | how   | result                         |
      | 0600 | adm   | adm   | adm    | write | succeeds                       |
      | 0600 | adm   | adm   | glenda | read  | fails with "permission denied" |
      | 0640 | adm   | dev   | bob    | read  | succeeds                       |
      | 0640 | adm   | dev   | bob    | write | fails with "permission denied" |
      | 0640 | adm   | dev   | glenda | read  | fails with "permission denied" |
      | 0644 | adm   | adm   | glenda | read  | succeeds                       |
      | 0644 | adm   | adm   | none   | read  | succeeds                       |
      | 0600 | none  | adm   | none   | read  | fails with "permission denied" |
      | 0604 | none  | adm   | none   | read  | succeeds                       |
      | 0755 | adm   | adm   | glenda | run   | succeeds                       |
      | 0754 | adm   | adm   | glenda | run   | fails with "permission denied" |
      | 0750 | adm   | dev   | bob    | run   | succeeds                       |
      | 0644 | adm   | adm   | glenda | read with truncation | fails with "permission denied" |
      | 0644 | adm   | adm   | glenda | read and write       | fails with "permission denied" |
      | 0640 | adm   | adm   | adm    | read and write       | succeeds                       |

  @FOG_GEFS_607
  Scenario: A directory opens only to read
    Then adm opening the root to write fails with "permission denied"
    And adm opening the root to read succeeds

  @FOG_GEFS_607
  Scenario: A file opened to be removed on close goes when it is closed
    When adm sets the root's mode to d0777 at time 150
    And adm creates directory tmp in the root with mode d0777 at time 200
    And adm creates file scratch in tmp with mode 0666 at time 200
    And glenda opens tmp/scratch to be removed on close, and closes it
    Then tmp lists nothing

  @FOG_GEFS_607
  Scenario Outline: Opening to remove on close needs what removing needs
    When adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file profile in usr with mode 0666 at time 200
    Then <who> opening <what> to be removed on close fails with "<error>"

    Examples:
      | who    | what        | error                  |
      | glenda | usr/profile | permission denied      |
      | adm    | usr         | directory is not empty |

  @FOG_GEFS_607
  Scenario: An exclusive file opens only once at a time
    When adm creates an exclusive file lock in the root with mode 0666 at time 200
    And adm opens lock to read
    Then adm opening lock to read fails with "open/create -- file is locked"
    When adm closes lock
    Then adm opening lock to read succeeds
    When adm creates file plain in the root with mode 0666 at time 200
    And adm opens plain to read
    Then adm opening plain to read succeeds

  @FOG_GEFS_608
  Scenario: Committed files are there when the device is opened again, and later changes are not
    When adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file notes in usr with mode 0644 at time 200
    And adm writes "kept" to usr/notes at offset 0 at time 300
    And the store commits
    And adm writes "lost" to usr/notes at offset 0 at time 400
    And adm creates file later in usr with mode 0644 at time 400
    And the device is opened again, serving main
    Then reading 100 bytes of usr/notes at offset 0 gives "kept"
    And usr lists notes
    And every block of the device is held, in a log or free

  @FOG_GEFS_609
  Scenario: Renaming moves an entry within its directory, and its handle follows
    When adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file notes in usr with mode 0644 at time 200
    And adm renames usr/notes to memo at time 300
    Then usr lists memo
    And walking notes from usr finds nothing
    And the renamed handle is a file "memo" owned by adm in group adm, mode 0644, version 1, modified at 200
    And usr is a directory "usr" owned by adm in group adm, mode d0775, version 2, modified at 200
    When adm renames usr to home at time 400
    Then the root lists home
    And home lists memo
    And walking .. from home/memo gives home

  @FOG_GEFS_609
  Scenario Outline: A wstat changes what it names, and records who changed it
    When adm creates file notes in the root with mode 0644 at time 200
    And adm writes "hello, world" to notes at offset 0 at time 300
    And adm sets <change> of notes at time 400
    Then notes is a file "notes" owned by <owner> in group <group>, mode <mode>, version 2, modified at <mtime>, <length> bytes long, last changed by adm

    Examples:
      | change              | owner  | group | mode | mtime | length |
      | the mode to 0600    | adm    | adm   | 0600 | 300   | 12     |
      | the mtime to 50     | adm    | adm   | 0644 | 50    | 12     |
      | the owner to glenda | glenda | adm   | 0644 | 300   | 12     |
      | the group to sys    | adm    | sys   | 0644 | 300   | 12     |
      | the length to 5     | adm    | adm   | 0644 | 300   | 5      |
      | the length to 20000 | adm    | adm   | 0644 | 300   | 20000  |
      | the length to 9223372036854775807 | adm | adm | 0644 | 300 | 9223372036854775807 |

  @FOG_GEFS_609
  Scenario: Shortening a file frees the blocks past its end, and lengthening it again reads zeros there
    When adm creates file data in the root with mode 0644 at time 200
    And adm writes 40000 bytes to data at offset 0 at time 300
    And the store commits
    And adm sets the length to 16390 of data at time 400
    And the store commits
    And adm sets the length to 40000 of data at time 500
    And the store commits
    Then reading 16390 bytes of data at offset 0 gives the first 16390 bytes written
    And reading 10 bytes of data at offset 16390 gives 10 zeros
    And reading 10 bytes of data at offset 32768 gives 10 zeros
    And reading 10 bytes of data at offset 39990 gives 10 zeros
    And every block of the device is held, in a log or free

  @FOG_GEFS_609
  Scenario Outline: A wstat refuses what gefs refuses, and changes nothing when it does
    When adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file notes in usr with mode 0644 at time 200
    And adm creates file other in usr with mode 0644 at time 200
    And adm sets usr/notes's mode to 0664, group to dev and owner to bob at time 200
    Then <who> setting <change> of <what> fails with "<error>"
    And usr/notes is a file "notes" owned by bob in group dev, mode 0664, version 1, modified at 200

    Examples:
      | who    | change                         | what      | error                                      |
      | glenda | the name to memo               | usr/notes | permission denied                          |
      | adm    | the name to other              | usr/notes | create/wstat -- file exists                |
      | adm    | the name to a/b                | usr/notes | create/wstat -- bad character in file name |
      | adm    | a name of 245 bytes            | usr/notes | name too long                              |
      | glenda | the length to 1                | usr/notes | permission denied                          |
      | adm    | the length to 1                | usr       | wstat -- attempt to make length negative   |
      | glenda | the mode to 0666               | usr/notes | wstat -- not owner or group leader         |
      | glenda | the mtime to 50                | usr/notes | wstat -- not owner or group leader         |
      | adm    | the mode to d0664              | usr/notes | wstat -- attempt to change directory       |
      | adm    | the mode to 0664 with unknown bits | usr/notes | wstat -- unknown bits in qid.type/mode |
      | bob    | the owner to glenda            | usr/notes | wstat -- not owner                         |
      | bob    | the group to sys               | usr/notes | wstat -- not in group                      |
      | glenda | the group to sys               | usr/notes | wstat -- not in group                      |
      | adm    | the last modifier to glenda    | usr/notes | wstat -- attempt to change muid            |
      | adm    | the qid                        | usr/notes | wstat -- attempt to change qid             |
      | adm    | the qid's path                 | usr/notes | wstat -- attempt to change qid             |
      | adm    | the qid's version              | usr/notes | wstat -- attempt to change qid             |
      | adm    | the length to 9223372036854775808 | usr/notes | wstat -- attempt to make length negative |
      | adm    | the owner to a name of 245 bytes | usr/notes | name too long                            |
      | adm    | the group to a name of 245 bytes | usr/notes | name too long                            |

  @FOG_GEFS_609
  Scenario Outline: A wstat that names what is already there needs no permission, and records who sent it
    When adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file notes in usr with mode 0644 at time 200
    And adm sets usr/notes's mode to 0664, group to dev and owner to bob at time 200
    And glenda sets <change> of usr/notes at time 300
    Then usr/notes is a file "notes" owned by bob in group dev, mode 0664, version 2, modified at 200, 0 bytes long, last changed by glenda

    Examples:
      | change            |
      | the length to 0   |
      | the mode to 0664  |
      | the mtime to 200  |
      | the owner to bob  |
      | the group to dev  |
      | the name to notes |

  @FOG_GEFS_609
  Scenario Outline: The owner, a group leader or a member of adm may change a mode or a group
    When adm creates file notes in the root with mode 0644 at time 200
    And adm sets notes's mode to 0644, group to <from> and owner to <owner> at time 200
    And <who> sets the <field> to <value> of notes at time 300
    Then notes's <field> is <value>

    Examples:
      | owner  | from | who    | field | value |
      | bob    | dev  | bob    | mode  | 0600  |
      | glenda | dev  | bob    | mode  | 0600  |
      | glenda | adm  | adm    | mode  | 0600  |
      | bob    | dev  | bob    | group | bob   |
      | adm    | dev  | bob    | group | bob   |
      | glenda | adm  | adm    | group | sys   |

  @FOG_GEFS_609
  Scenario: An owner or group may be named in as many bytes as a file
    When adm creates file notes in the root with mode 0644 at time 200
    And adm sets the owner to a name of 244 bytes of notes at time 300
    And adm sets the group to a name of 244 bytes of notes at time 300
    Then notes's owner is a name of 244 bytes
    And notes's group is a name of 244 bytes

  @FOG_GEFS_609
  Scenario: A wstat that changes nothing commits the store
    When adm creates file notes in the root with mode 0644 at time 200
    And adm sets nothing of notes
    Then the store has committed once since it was reamed
    And notes is a file "notes" owned by adm in group adm, mode 0644, version 0, modified at 200
