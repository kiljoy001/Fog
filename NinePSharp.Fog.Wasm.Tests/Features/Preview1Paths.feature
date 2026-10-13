@fog_wasm_preview1_paths
Feature: Preview 1 paths resolve beneath the directories the application holds
  The directories the parent left open are the preopens, under the names they were opened by. A
  path is resolved beneath the directory descriptor it is given with: it is cleaned first, and an
  absolute path, or a .. that climbs above the directory, is an escape, NOTCAPABLE, so nothing is
  learned about what lies outside. The directory's true path is then resolved by name through the
  namespace as it is now, and what it names must be the directory the application was given, or
  the call is NOENT: a bind beneath a held directory applies, a bind over it, or its replacement,
  does not. The application's own renames keep the directories it holds usable. path_open opens
  for reading, writing or both as its rights ask, and what it opens gets no right the directory
  does not pass on; a directory opens with a directory's rights, and asking to write or truncate
  one is ISDIR. Creating needs path_create_file on the directory, truncating
  path_filestat_set_size. A trailing slash names a directory, so on anything else it is NOTDIR.
  CREAT opens what exists, so its contents are kept unless TRUNC is given, and
  with EXCL what exists is EXIST. Removing checks the file's type first. A rename is a wstat of
  the name within one directory; across directories it is NOTSUP rather than a copy, and onto an
  existing name EXIST, since Plan 9 does not replace. Plan 9 has no links or symlinks, so those
  calls are NOTSUP. fd_readdir lists . and .. first, then the directory's entries in union order,
  each cookie counting the entries before it.

  Background:
    Given a kernel that runs WASM applications
    And the directory "/data"
    And the file "/data/report" holds "daily"
    And the directory "/data/sub"
    And the file "/data/sub/f" holds "f"
    And the interpreter has "/data" open for reading as descriptor 3
    And the interpreter has "/data/report" open for reading as descriptor 4

  @WASI_NS_016
  Scenario: The directories the parent left open are the preopens, under the names they were opened by
    When the trace interpreter runs:
      """
      fd_prestat_get 3
      fd_prestat_dir_name 3 5
      fd_prestat_dir_name 3 4
      fd_prestat_get 4
      fd_prestat_dir_name 4 5
      fd_prestat_get 0
      fd_prestat_get 9
      path_open 3 0 "report" 0 2 0 0
      fd_prestat_get 2
      path_open 3 0 "sub" 2 0x2000 0 0
      fd_prestat_get 5
      """
    Then the trace is:
      """
      fd_prestat_get 3 -> ok tag=0 len=5
      fd_prestat_dir_name 3 5 -> ok "/data"
      fd_prestat_dir_name 3 4 -> NAMETOOLONG
      fd_prestat_get 4 -> BADF
      fd_prestat_dir_name 4 5 -> BADF
      fd_prestat_get 0 -> BADF
      fd_prestat_get 9 -> BADF
      path_open 3 0 "report" 0 2 0 0 -> ok 2
      fd_prestat_get 2 -> BADF
      path_open 3 0 "sub" 2 0x2000 0 0 -> ok 5
      fd_prestat_get 5 -> BADF
      """

  @WASI_NS_002 @security
  Scenario: A path cannot leave the directory it is resolved from
    When the trace interpreter runs:
      """
      path_open 3 0 "../etc" 0 2 0 0
      path_open 3 0 "/etc" 0 2 0 0
      path_open 3 0 "sub/../../data/report" 0 2 0 0
      path_open 3 0 ".." 0 2 0 0
      path_open 3 0 "sub//../.." 0 2 0 0
      path_open 3 0 "" 0 2 0 0
      path_open 3 0 "\xff" 0 2 0 0
      """
    Then the trace is:
      """
      path_open 3 0 "../etc" 0 2 0 0 -> NOTCAPABLE
      path_open 3 0 "/etc" 0 2 0 0 -> NOTCAPABLE
      path_open 3 0 "sub/../../data/report" 0 2 0 0 -> NOTCAPABLE
      path_open 3 0 ".." 0 2 0 0 -> NOTCAPABLE
      path_open 3 0 "sub//../.." 0 2 0 0 -> NOTCAPABLE
      path_open 3 0 "" 0 2 0 0 -> NOENT
      path_open 3 0 "\xff" 0 2 0 0 -> ILSEQ
      """

  @WASI_NS_021
  Scenario: A path may walk back up within its directory
    When the trace interpreter runs:
      """
      path_open 3 0 "sub/./../report" 0 2 0 0
      fd_read 2 10
      path_open 3 0 "." 0 0x4000 0 0
      fd_readdir 5 4096 2
      """
    Then the trace is:
      """
      path_open 3 0 "sub/./../report" 0 2 0 0 -> ok 2
      fd_read 2 10 -> ok "daily"
      path_open 3 0 "." 0 0x4000 0 0 -> ok 5
      fd_readdir 5 4096 2 -> ok used=* "report":4:3:* "sub":3:4:*
      """

  @WASI_NS_005
  Scenario: path_open opens files and directories beneath the directory
    When the trace interpreter runs:
      """
      path_open 3 0 "sub" 2 0x7bffe19 0x3fffffff 0
      path_open 2 0 "f" 0 2 0 0
      fd_read 5 10
      path_open 3 0 "report" 2 2 0 0
      path_open 3 0 "report" 0 2 0 0
      path_open 4 0 "x" 0 2 0 0
      path_open 9 0 "x" 0 2 0 0
      path_open 3 0 "missing" 0 2 0 0
      """
    Then the trace is:
      """
      path_open 3 0 "sub" 2 0x7bffe19 0x3fffffff 0 -> ok 2
      path_open 2 0 "f" 0 2 0 0 -> ok 5
      fd_read 5 10 -> ok "f"
      path_open 3 0 "report" 2 2 0 0 -> NOTDIR
      path_open 3 0 "report" 0 2 0 0 -> ok 6
      path_open 4 0 "x" 0 2 0 0 -> NOTDIR
      path_open 9 0 "x" 0 2 0 0 -> BADF
      path_open 3 0 "missing" 0 2 0 0 -> NOENT
      """

  @WASI_NS_005
  Scenario Outline: WASI open flags keep their distinct meanings
    When the trace interpreter runs:
      """
      path_open 3 0 "<name>" <oflags> 0x42 0 0
      fd_write 2 "new"
      """
    Then the trace is:
      """
      path_open 3 0 "<name>" <oflags> 0x42 0 0 -> <opened>
      fd_write 2 "new" -> <written>
      """
    And the file "/data/<name>" holds "<contents>"

    Examples:
      | name   | oflags | opened | written | contents |
      | report | 0      | ok 2   | ok 3    | newly    |
      | report | 1      | ok 2   | ok 3    | newly    |
      | report | 8      | ok 2   | ok 3    | new      |
      | report | 9      | ok 2   | ok 3    | new      |
      | report | 5      | EXIST  | BADF    | daily    |
      | report | 4      | ok 2   | ok 3    | newly    |
      | fresh  | 1      | ok 2   | ok 3    | new      |
      | fresh  | 5      | ok 2   | ok 3    | new      |

  @WASI_NS_005
  Scenario: A directory opens with a directory's rights, and asking to write or truncate it is ISDIR
    When the trace interpreter runs:
      """
      path_open 3 0 "sub" 0 0x7bffe1b 0x3fffffff 0
      fd_fdstat_get 2
      path_open 3 0 "sub" 0 0x40 0 0
      path_open 3 0 "sub" 0 0x3fffffff 0 0
      path_open 3 0 "sub" 8 0x2 0 0
      """
    Then the trace is:
      """
      path_open 3 0 "sub" 0 0x7bffe1b 0x3fffffff 0 -> ok 2
      fd_fdstat_get 2 -> ok filetype=3 flags=0 base=0x7bffe19 inheriting=0x3fffffff
      path_open 3 0 "sub" 0 0x40 0 0 -> ISDIR
      path_open 3 0 "sub" 0 0x3fffffff 0 0 -> ISDIR
      path_open 3 0 "sub" 8 0x2 0 0 -> ISDIR
      """

  @WASI_NS_005
  Scenario: path_open opens for reading, writing or both, as the rights ask
    Given the write-only file "/data/drop" holds ""
    When the trace interpreter runs:
      """
      path_open 3 0 "drop" 0 0x40 0 0
      fd_write 2 "in"
      path_open 3 0 "drop" 0 0x42 0 0
      path_open 3 0 "report" 0 0x42 0 0
      fd_read 5 5
      """
    Then the trace is:
      """
      path_open 3 0 "drop" 0 0x40 0 0 -> ok 2
      fd_write 2 "in" -> ok 2
      path_open 3 0 "drop" 0 0x42 0 0 -> ACCES
      path_open 3 0 "report" 0 0x42 0 0 -> ok 5
      fd_read 5 5 -> ok "daily"
      """

  @WASI_NS_020 @security
  Scenario: Every right a preopen has is still checked by the file server
    Given the read-only file "/data/locked" holds "kept"
    When the trace interpreter runs:
      """
      path_open 3 0 "locked" 0 0x42 0 0
      path_open 3 0 "locked" 0 0x2 0 0
      fd_read 2 10
      """
    Then the trace is:
      """
      path_open 3 0 "locked" 0 0x42 0 0 -> ACCES
      path_open 3 0 "locked" 0 0x2 0 0 -> ok 2
      fd_read 2 10 -> ok "kept"
      """
    And the file "/data/locked" holds "kept"

  @WASI_NS_004
  Scenario: What path_open opens gets the rights asked for, none the directory does not pass on
    When the trace interpreter runs:
      """
      path_open 3 0 "report" 0 0x2 0x2 0
      fd_fdstat_get 2
      fd_write 2 "x"
      fd_fdstat_set_rights 3 0x7bffe19 0x2
      path_open 3 0 "report" 0 0x42 0 0
      path_open 3 0 "report" 0 0x2 0x42 0
      """
    Then the trace is:
      """
      path_open 3 0 "report" 0 0x2 0x2 0 -> ok 2
      fd_fdstat_get 2 -> ok filetype=4 flags=0 base=0x2 inheriting=0x2
      fd_write 2 "x" -> NOTCAPABLE
      fd_fdstat_set_rights 3 0x7bffe19 0x2 -> ok
      path_open 3 0 "report" 0 0x42 0 0 -> NOTCAPABLE
      path_open 3 0 "report" 0 0x2 0x42 0 -> NOTCAPABLE
      """

  @WASI_NS_004
  Scenario: Truncating needs path_filestat_set_size on the directory, and creating path_create_file
    When the trace interpreter runs:
      """
      fd_fdstat_set_rights 3 0x7b7fa19 0x3fffffff
      path_open 3 0 "report" 8 0x40 0 0
      path_open 3 0 "fresh" 1 0x40 0 0
      path_open 3 0 "report" 1 0x2 0 0
      path_open 3 0 "report" 0 0x2 0 0
      fd_read 2 5
      """
    Then the trace is:
      """
      fd_fdstat_set_rights 3 0x7b7fa19 0x3fffffff -> ok
      path_open 3 0 "report" 8 0x40 0 0 -> NOTCAPABLE
      path_open 3 0 "fresh" 1 0x40 0 0 -> NOTCAPABLE
      path_open 3 0 "report" 1 0x2 0 0 -> NOTCAPABLE
      path_open 3 0 "report" 0 0x2 0 0 -> ok 2
      fd_read 2 5 -> ok "daily"
      """

  @WASI_NS_013
  Scenario: path_open keeps APPEND and NONBLOCK, and refuses a sync flag as NOTSUP
    When the trace interpreter runs:
      """
      path_open 3 0 "report" 0 0x46 0 1
      fd_fdstat_get 2
      fd_write 2 "!"
      path_open 3 0 "." 0 0x4000 0 4
      fd_fdstat_get 5
      path_open 3 0 "report" 0 0x2 0 2
      path_open 3 0 "report" 0 0x2 0 8
      path_open 3 0 "report" 0 0x2 0 16
      """
    Then the trace is:
      """
      path_open 3 0 "report" 0 0x46 0 1 -> ok 2
      fd_fdstat_get 2 -> ok filetype=4 flags=1 base=0x46 inheriting=0x0
      fd_write 2 "!" -> ok 1
      path_open 3 0 "." 0 0x4000 0 4 -> ok 5
      fd_fdstat_get 5 -> ok filetype=3 flags=4 base=0x4000 inheriting=0x0
      path_open 3 0 "report" 0 0x2 0 2 -> NOTSUP
      path_open 3 0 "report" 0 0x2 0 8 -> NOTSUP
      path_open 3 0 "report" 0 0x2 0 16 -> NOTSUP
      """
    And the file "/data/report" holds "daily!"

  @WASI_NS_002
  Scenario: A trailing slash names a directory, so on anything else it is NOTDIR
    When the trace interpreter runs:
      """
      path_open 3 0 "report/" 0 0x2 0 0
      path_open 3 0 "report///" 0 0x2 0 0
      path_filestat_get 3 0 "report/"
      path_unlink_file 3 "report/"
      path_open 3 0 "sub/" 0 0x4000 0 0
      path_create_directory 3 "new/"
      path_filestat_get 3 0 "new"
      """
    Then the trace is:
      """
      path_open 3 0 "report/" 0 0x2 0 0 -> NOTDIR
      path_open 3 0 "report///" 0 0x2 0 0 -> NOTDIR
      path_filestat_get 3 0 "report/" -> NOTDIR
      path_unlink_file 3 "report/" -> NOTDIR
      path_open 3 0 "sub/" 0 0x4000 0 0 -> ok 2
      path_create_directory 3 "new/" -> ok
      path_filestat_get 3 0 "new" -> ok dev=0 ino=* filetype=3 nlink=1 size=0 atim=* mtim=* ctim=*
      """

  @WASI_NS_003 @security
  Scenario Outline: A directory the application holds stays the one it was given
    Given the file "/n/extra/report" holds "extra"
    And the directory "/secret"
    And the file "/secret/report" holds "secret"
    And once the interpreter is ready, the parent <change>
    When the trace interpreter runs:
      """
      fd_write 7 "ready"
      fd_read 6 2
      path_open 3 0 "report" 0 2 0 0
      fd_read 2 10
      """
    Then the trace is:
      """
      fd_write 7 "ready" -> ok 5
      fd_read 6 2 -> ok "go"
      path_open 3 0 "report" 0 2 0 0 -> <opened>
      fd_read 2 10 -> <read>
      """

    Examples:
      | change                                   | opened | read          |
      | binds "/n/extra/report" over "/data/report" | ok 2   | ok "extra"    |
      | binds "/secret" over "/data"             | NOENT  | BADF          |
      | renames "/data" to "/old"                | NOENT  | BADF          |

  @WASI_NS_003 @security
  Scenario: A directory the application holds that is replaced is NOENT, even if the new one has the name
    Given the directory "/data/held"
    And the file "/data/held/f" holds "old"
    And once the interpreter is ready, the parent removes "/data/held/f" and then "/data/held" and makes a new one holding "f"
    And the interpreter has "/data/held" open for reading as descriptor 5
    When the trace interpreter runs:
      """
      fd_write 7 "ready"
      fd_read 6 2
      path_open 5 0 "f" 0 2 0 0
      path_open 3 0 "held/f" 0 2 0 0
      """
    Then the trace is:
      """
      fd_write 7 "ready" -> ok 5
      fd_read 6 2 -> ok "go"
      path_open 5 0 "f" 0 2 0 0 -> NOENT
      path_open 3 0 "held/f" 0 2 0 0 -> ok 2
      """

  @WASI_NS_003
  Scenario: A directory the application renames stays usable under its new name
    Given the file "/data/subway/g" holds "g"
    When the trace interpreter runs:
      """
      path_open 3 0 "sub" 2 0x2002 0x3fffffff 0
      path_open 3 0 "subway" 2 0x2002 0x3fffffff 0
      path_rename 3 "sub" 3 "moved"
      path_open 2 0 "f" 0 2 0 0
      fd_read 6 10
      path_open 5 0 "g" 0 2 0 0
      path_rename 3 "moved" 3 "sub"
      path_open 2 0 "f" 0 2 0 0
      """
    Then the trace is:
      """
      path_open 3 0 "sub" 2 0x2002 0x3fffffff 0 -> ok 2
      path_open 3 0 "subway" 2 0x2002 0x3fffffff 0 -> ok 5
      path_rename 3 "sub" 3 "moved" -> ok
      path_open 2 0 "f" 0 2 0 0 -> ok 6
      fd_read 6 10 -> ok "f"
      path_open 5 0 "g" 0 2 0 0 -> ok 7
      path_rename 3 "moved" 3 "sub" -> ok
      path_open 2 0 "f" 0 2 0 0 -> ok 8
      """

  @WASI_NS_003
  Scenario: Closing and renumbering move what the application holds with the descriptor
    When the trace interpreter runs:
      """
      path_open 3 0 "sub" 2 0x7bffe19 0x3fffffff 0
      fd_renumber 2 8
      path_open 8 0 "f" 0 0x2002 0 0
      path_open 2 0 "f" 0 2 0 0
      fd_close 2
      fd_close 8
      path_open 8 0 "f" 0 2 0 0
      path_open 3 0 "sub" 2 0x7bffe19 0x3fffffff 0
      fd_close 2
      path_open 3 0 "report" 0 0x2002 0 0
      path_open 2 0 "f" 0 2 0 0
      path_open 3 0 "sub" 2 0x7bffe19 0x3fffffff 0
      fd_renumber 2 5
      path_open 5 0 "f" 0 2 0 0
      """
    Then the trace is:
      """
      path_open 3 0 "sub" 2 0x7bffe19 0x3fffffff 0 -> ok 2
      fd_renumber 2 8 -> ok
      path_open 8 0 "f" 0 0x2002 0 0 -> ok 2
      path_open 2 0 "f" 0 2 0 0 -> NOTDIR
      fd_close 2 -> ok
      fd_close 8 -> ok
      path_open 8 0 "f" 0 2 0 0 -> BADF
      path_open 3 0 "sub" 2 0x7bffe19 0x3fffffff 0 -> ok 2
      fd_close 2 -> ok
      path_open 3 0 "report" 0 0x2002 0 0 -> ok 2
      path_open 2 0 "f" 0 2 0 0 -> NOTDIR
      path_open 3 0 "sub" 2 0x7bffe19 0x3fffffff 0 -> ok 5
      fd_renumber 2 5 -> ok
      path_open 5 0 "f" 0 2 0 0 -> NOTDIR
      """

  @WASI_NS_013
  Scenario: Directories are made and removed, and files unlinked, after their type is checked
    When the trace interpreter runs:
      """
      path_create_directory 3 "made"
      path_create_directory 3 "made"
      path_create_directory 3 "report"
      path_remove_directory 3 "report"
      path_unlink_file 3 "sub"
      path_remove_directory 3 "sub"
      path_remove_directory 3 "made"
      path_remove_directory 3 "made"
      path_unlink_file 3 "sub/f"
      path_unlink_file 3 "sub/f"
      path_remove_directory 3 "sub"
      """
    Then the trace is:
      """
      path_create_directory 3 "made" -> ok
      path_create_directory 3 "made" -> EXIST
      path_create_directory 3 "report" -> EXIST
      path_remove_directory 3 "report" -> NOTDIR
      path_unlink_file 3 "sub" -> ISDIR
      path_remove_directory 3 "sub" -> NOTEMPTY
      path_remove_directory 3 "made" -> ok
      path_remove_directory 3 "made" -> NOENT
      path_unlink_file 3 "sub/f" -> ok
      path_unlink_file 3 "sub/f" -> NOENT
      path_remove_directory 3 "sub" -> ok
      """

  @WASI_NS_013
  Scenario: A rename stays within one directory and does not replace
    When the trace interpreter runs:
      """
      path_rename 3 "report" 3 "weekly"
      path_rename 3 "weekly" 3 "sub/weekly"
      path_rename 3 "sub/f" 3 "sux/f"
      path_rename 3 "weekly" 3 "sub"
      path_rename 3 "missing" 3 "other"
      """
    Then the trace is:
      """
      path_rename 3 "report" 3 "weekly" -> ok
      path_rename 3 "weekly" 3 "sub/weekly" -> NOTSUP
      path_rename 3 "sub/f" 3 "sux/f" -> NOTSUP
      path_rename 3 "weekly" 3 "sub" -> EXIST
      path_rename 3 "missing" 3 "other" -> NOENT
      """
    And the file "/data/weekly" holds "daily"

  @WASI_NS_026
  Scenario: A path's file status is got and its modification time set, the file server keeping its whole seconds
    When the trace interpreter runs:
      """
      path_filestat_get 3 0 "report"
      path_filestat_set_times 3 0 "report" 0 1700000000500000000 4
      path_filestat_get 3 0 "report"
      path_filestat_set_times 3 0 "report" 0 0 0
      path_filestat_get 3 0 "sub"
      path_filestat_get 3 0 "missing"
      path_filestat_set_times 3 0 "missing" 0 0 4
      path_filestat_set_times 3 0 "missing" 0 0 0
      path_filestat_set_times 3 0 "report" 0 0 1
      path_filestat_set_times 3 0 "report" 0 0 3
      """
    Then the trace is:
      """
      path_filestat_get 3 0 "report" -> ok dev=0 ino=* filetype=4 nlink=1 size=5 atim=1790856000000000000 mtim=1790856000000000000 ctim=1790856000000000000
      path_filestat_set_times 3 0 "report" 0 1700000000500000000 4 -> ok
      path_filestat_get 3 0 "report" -> ok dev=0 ino=* filetype=4 nlink=1 size=5 atim=* mtim=1700000000500000000 ctim=1700000000500000000
      path_filestat_set_times 3 0 "report" 0 0 0 -> ok
      path_filestat_get 3 0 "sub" -> ok dev=0 ino=* filetype=3 nlink=1 size=0 atim=* mtim=* ctim=*
      path_filestat_get 3 0 "missing" -> NOENT
      path_filestat_set_times 3 0 "missing" 0 0 4 -> NOENT
      path_filestat_set_times 3 0 "missing" 0 0 0 -> NOENT
      path_filestat_set_times 3 0 "report" 0 0 1 -> NOTSUP
      path_filestat_set_times 3 0 "report" 0 0 3 -> INVAL
      """
    And the file "/data/report" was last modified at 1700000000

  @WASI_NS_013
  Scenario: Plan 9 has no links or symlinks
    When the trace interpreter runs:
      """
      path_link 3 0 "report" 3 "link"
      path_symlink "report" 3 "link"
      path_readlink 3 "report" 10
      """
    Then the trace is:
      """
      path_link 3 0 "report" 3 "link" -> NOTSUP
      path_symlink "report" 3 "link" -> NOTSUP
      path_readlink 3 "report" 10 -> NOTSUP
      """

  @WASI_NS_010
  Scenario: fd_readdir lists . and .. first, then the entries in union order, from any cookie
    Given the directory "/n/b"
    And the file "/n/b/zz" holds "z"
    And "/n/b" is bound after "/data"
    When the trace interpreter runs:
      """
      fd_readdir 3 4096 0
      fd_readdir 3 4096 3
      fd_readdir 3 30 0
      fd_readdir 3 4096 5
      fd_readdir 4 4096 0
      fd_filestat_get 3
      path_filestat_get 3 0 "report"
      """
    Then the trace is:
      """
      fd_readdir 3 4096 0 -> ok used=* ".":3:1:* "..":3:2:0 "report":4:3:* "sub":3:4:* "zz":4:5:*
      fd_readdir 3 4096 3 -> ok used=* "sub":3:4:* "zz":4:5:*
      fd_readdir 3 30 0 -> ok used=30 ".":3:1:* partial
      fd_readdir 3 4096 5 -> ok used=0
      fd_readdir 4 4096 0 -> NOTDIR
      fd_filestat_get 3 -> ok *
      path_filestat_get 3 0 "report" -> ok *
      """
    And fd_readdir gives "." the inode fd_filestat_get gives descriptor 3, and "report" the one path_filestat_get gives it

  @WASI_NS_004
  Scenario: Each path call needs its right on the directory
    When the trace interpreter runs:
      """
      fd_fdstat_set_rights 3 0 0
      path_open 3 0 "report" 0 0 0 0
      path_create_directory 3 "x"
      path_remove_directory 3 "sub"
      path_unlink_file 3 "report"
      path_rename 3 "report" 3 "x"
      path_filestat_get 3 0 "report"
      path_filestat_set_times 3 0 "report" 0 0 0
      fd_readdir 3 4096 0
      """
    Then the trace is:
      """
      fd_fdstat_set_rights 3 0 0 -> ok
      path_open 3 0 "report" 0 0 0 0 -> NOTCAPABLE
      path_create_directory 3 "x" -> NOTCAPABLE
      path_remove_directory 3 "sub" -> NOTCAPABLE
      path_unlink_file 3 "report" -> NOTCAPABLE
      path_rename 3 "report" 3 "x" -> NOTCAPABLE
      path_filestat_get 3 0 "report" -> NOTCAPABLE
      path_filestat_set_times 3 0 "report" 0 0 0 -> NOTCAPABLE
      fd_readdir 3 4096 0 -> NOTCAPABLE
      """
