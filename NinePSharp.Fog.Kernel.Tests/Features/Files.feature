@fog_kernel_files
Feature: stat, seek and bind act on files and the namespace as 9front's do
  stat(2) reads a file's directory entry: its name as the path names it, its length and its
  permissions. seek(2) moves a descriptor's offset from the start, from where it is or from the end,
  but not before the start; a directory can only go back to its start, from which it is read again.
  bind(2) makes a file or directory visible at another name in the process's namespace, replacing
  what is there or joining a union before or after it. fd2path(2) gives the rooted name a descriptor
  was opened or created by, cleaned as cleanname does, which later changes to the namespace do not
  update; a pipe's ends are named as devpipe names them. A ram file opens only for the access its
  permissions give the user, as lib9p's sopen and hasperm decide: the other bits, then the owner's,
  then the group's, each user leading a group of its own name; truncating needs write. Every call that
  names a file checks the name first, as namec does: an empty name is refused, then one of 64 KiB of
  UTF-8 or more, then one holding a control character or DEL, named with the name quoted as %q quotes
  it, in rc's way, within the 128 bytes of the message. A path ends at its NUL in 9front; a name here
  may hold one, and NUL is a bad character, as isfrog has it. wstat and fwstat check a new name before
  anything else, refusing a slash in it too, unless it is "/" or 64 bytes or longer, which is left to
  the file server as validstat leaves it. bind resolves its first name before checking its second.
  Fog also refuses a name made only of white space, which 9front takes: an element of a path, or a
  new name of any length, that is all spaces is a blank file name, checked after namec's own checks.

  Background:
    Given a booted kernel
    And "/tmp/f" holds "hello"

  @FOG_KERNEL_017
  Scenario Outline: stat reads a file's name, length and permissions
    When the process stats "<path>"
    Then the entry is named "<name>", is <length> bytes long and has the permissions <permissions>

    Examples:
      | path      | name | length | permissions |
      | /tmp/f    | f    | 5      | 0666        |
      | /tmp      | tmp  | 0      | d0777       |
      | /dev/null | null | 0      | 0666        |

  @FOG_KERNEL_017
  Scenario: stat of a missing file fails as open does
    When the process stats "/tmp/missing/x"
    Then the call fails with "file does not exist: '/tmp/missing'"

  @FOG_KERNEL_018
  Scenario Outline: seek moves the offset the next read starts at
    Given the process has "/tmp/f" open as descriptor 0
    When it seeks descriptor 0 to <offset> from <whence>
    Then the seek returns <position>
    And reading descriptor 0 gives "<rest>"

    Examples:
      | offset | whence          | position | rest |
      | 1      | the start       | 1        | ello |
      | -2     | the end         | 3        | lo   |
      | 2      | where it is     | 2        | llo  |

  @FOG_KERNEL_018
  Scenario Outline: Seeking before a file's start fails, as 9front's Enegoff
    Given the process has "/tmp/f" open as descriptor 0
    When it seeks descriptor 0 to <offset> from <whence>
    Then the call fails with "negative i/o offset"

    Examples:
      | offset | whence      |
      | -1     | the start   |
      | -6     | the end     |

  @FOG_KERNEL_018
  Scenario: Seeking a directory to its start reads it again from its first entry
    Given "/tmp/g" holds "x"
    And the process has "/tmp" open for reading as descriptor 0
    And it has listed descriptor 0
    When it seeks descriptor 0 to 0 from the start
    Then listing descriptor 0 gives "f g"

  @FOG_KERNEL_018
  Scenario Outline: Seeking a directory anywhere but its start fails
    Given the process has "/tmp" open for reading as descriptor 0
    When it seeks descriptor 0 to <offset> from <whence>
    Then the call fails with "file is a directory"

    Examples:
      | offset | whence      |
      | 1      | the start   |
      | 0      | where it is |
      | 0      | the end     |

  @FOG_KERNEL_019
  Scenario: bind makes a file visible at another name
    Given "/tmp/g" holds "other"
    When the process binds "/tmp/g" onto "/tmp/f"
    Then reading "/tmp/f" gives "other"

  @FOG_KERNEL_019
  Scenario Outline: bind of a directory with -b or -a makes a union in that order
    Given "/tmp/d/a" holds "1" in a new directory
    And "/tmp/e/b" holds "2" in a new directory
    When the process binds "/tmp/e" onto "/tmp/d" with <flag>
    Then listing "/tmp/d" gives "<names>"

    Examples:
      | flag | names |
      | -b   | b a   |
      | -a   | a b   |

  @FOG_KERNEL_019
  Scenario: bind -c lets files be created in the union's first member that allows it
    Given "/tmp/d/a" holds "1" in a new directory
    And "/tmp/e/b" holds "2" in a new directory
    When the process binds "/tmp/e" onto "/tmp/d" with -bc
    And the process creates "/tmp/d/c" holding "3"
    Then reading "/tmp/e/c" gives "3"

  @FOG_KERNEL_019
  Scenario Outline: bind of or onto a missing file fails as open does
    When the process binds "<name>" onto "<old>"
    Then the call fails with "file does not exist: '<missing>'"

    Examples:
      | name         | old          | missing      |
      | /tmp/missing | /tmp/f       | /tmp/missing |
      | /tmp/f       | /tmp/missing | /tmp/missing |
      | /tmp/f       | /tmp/x/y     | /tmp/x       |

  @FOG_KERNEL_020
  Scenario Outline: A child given its own descriptors, environment or namespace leaves the parent's alone
    Given the process has "/tmp/f" open as descriptor 0
    And "/env/v" holds "old"
    When a child <given> <flag> and then <change>
    Then the parent <sees>

    Examples:
      | given                                   | flag     | change                          | sees                        |
      | sharing all of them calls rfork with    | RFFDG    | closes descriptor 0             | still has descriptor 0      |
      | sharing all of them calls rfork with    | RFCFDG   | finds descriptor 0 closed       | still has descriptor 0      |
      | sharing all of them calls rfork with    | RFENVG   | writes "new" to "/env/v"        | reads "old" from "/env/v"   |
      | sharing all of them calls rfork with    | RFCENVG  | finds "/env/v" missing          | reads "old" from "/env/v"   |
      | sharing all of them calls rfork with    | RFNAMEG  | binds "/dev/null" onto "/tmp/f" | reads "hello" from "/tmp/f" |
      | sharing all of them calls rfork with    | RFCNAMEG | finds "/dev/null" missing       | reads "hello" from "/tmp/f" |
      | is forked with                          | RFCFDG   | finds descriptor 0 closed       | still has descriptor 0      |
      | is forked with                          | RFNAMEG  | binds "/dev/null" onto "/tmp/f" | reads "hello" from "/tmp/f" |
      | is forked with                          | RFCNAMEG | finds "/dev/null" missing       | reads "hello" from "/tmp/f" |

  @FOG_KERNEL_020
  Scenario: Without its own namespace, a child's bind is the parent's
    When a child sharing all of them calls rfork with RFENVG and then binds "/dev/null" onto "/tmp/f"
    Then the parent reads "" from "/tmp/f"

  @FOG_KERNEL_020
  Scenario Outline: rfork may not both copy and clear a group, nor share memory or skip waiting without a new process
    When the process calls rfork with <flags>
    Then the call fails with "bad arg in system call"

    Examples:
      | flags                |
      | RFFDG and RFCFDG     |
      | RFNAMEG and RFCNAMEG |
      | RFENVG and RFCENVG   |
      | RFMEM                |
      | RFNOWAIT             |

  @FOG_KERNEL_021
  Scenario Outline: A process with all 5000 descriptors in use cannot open, create or pipe
    Given the process has 5000 descriptors open
    When the process <call>
    Then the call fails with "no free file descriptors"

    Examples:
      | call               |
      | opens "/dev/null"  |
      | creates "/tmp/new" |
      | makes a pipe       |

  @FOG_KERNEL_021
  Scenario: Creating in a directory bound over without -c fails
    When the process binds "/dev" onto "/tmp"
    And the process creates "/tmp/x" holding "y"
    Then the call fails with "mounted directory forbids creation: '/tmp/x'"

  @FOG_KERNEL_021
  Scenario: /dev, where devcons is bound after without -c, forbids creation
    When the process creates "/dev/x" holding "y"
    Then the call fails with "mounted directory forbids creation: '/dev/x'"

  @FOG_KERNEL_021
  Scenario: A pipe that can have only one descriptor gives it back
    Given the process has 4999 descriptors open
    When the process makes a pipe
    Then the call fails with "no free file descriptors"
    And the process can still open "/dev/null"
    And no pipe is left

  @FOG_KERNEL_019
  Scenario: bind of a file onto a directory is an inconsistent mount
    When the process binds "/tmp/f" onto "/dev"
    Then the call fails with "inconsistent mount"

  @FOG_KERNEL_019
  Scenario: A union of files is an inconsistent mount
    Given "/tmp/g" holds "other"
    When the process binds "/tmp/f" onto "/tmp/g" with -a
    Then the call fails with "inconsistent mount"

  @FOG_KERNEL_022
  Scenario: A file opened ORCLOSE is removed when it is closed, as ramfs removes it
    Given the process has "/tmp/f" open ORCLOSE as descriptor 0
    When the process closes descriptor 0
    And the process stats "/tmp/f"
    Then the call fails with "file does not exist: '/tmp/f'"

  @FOG_KERNEL_022
  Scenario: A file opened ORCLOSE stays while a dup of its descriptor is open
    Given the process has "/tmp/f" open ORCLOSE as descriptor 0
    When the process dups descriptor 0
    And the process closes descriptor 0
    And the process stats "/tmp/f"
    Then the entry is named "f", is 5 bytes long and has the permissions 0666

  @FOG_KERNEL_022
  Scenario Outline: A directory opened ORCLOSE stays when it has files in it or is the root
    Given the process has "<path>" open ORCLOSE as descriptor 0
    When the process closes descriptor 0
    And the process stats "<path>"
    Then the entry is named "<name>", is 0 bytes long and has the permissions <permissions>

    Examples:
      | path | name | permissions |
      | /tmp | tmp  | d0777       |
      | /    | /    | d0555       |

  @FOG_KERNEL_025
  Scenario: Creating in a file fails, as ramfs refuses a create in a non-directory
    When the process creates "/tmp/f/x" holding "y"
    Then the call fails with "create in non-directory: '/tmp/f/x'"

  @FOG_KERNEL_032
  Scenario: fd2path gives the rooted name a descriptor was opened by, to every copy of it
    Given "/tmp/d/f" holds "x" in a new directory
    And the process has "/tmp/d/f" open as descriptor 0
    When the process dups descriptor 0
    Then fd2path of descriptor 0 is "/tmp/d/f"
    And fd2path of descriptor 1 is "/tmp/d/f"

  @FOG_KERNEL_032
  Scenario Outline: A name is rooted at the current directory and cleaned
    Given "/tmp/d/f" holds "x" in a new directory
    When the process changes to "<directory>"
    And the process opens "<path>"
    Then fd2path of descriptor 0 is "<name>"

    Examples:
      | directory | path        | name     |
      | /tmp      | d/../d/./f  | /tmp/d/f |
      | /tmp/d    | ../../tmp/d | /tmp/d   |
      | /tmp      | ../../..    | /        |
      | /tmp      | /tmp//d/    | /tmp/d   |

  @FOG_KERNEL_032
  Scenario: A created file is named by the name it was created by
    When the process creates "/tmp/new"
    Then fd2path of descriptor 0 is "/tmp/new"

  @FOG_KERNEL_032
  Scenario: A pipe's ends are named as devpipe names them
    When the process makes a pipe
    Then fd2path of descriptor 0 is "#|/data"
    And fd2path of descriptor 1 is "#|/data1"

  @FOG_KERNEL_032
  Scenario: fd2path of a descriptor that is not open fails
    Then fd2path of descriptor 5 fails with "fd out of range or not open"

  @FOG_KERNEL_018
  Scenario Outline: A directory opens only for reading, as devopen allows
    When the process opens "/tmp" for <mode>
    Then the call fails with "permission denied"

    Examples:
      | mode                |
      | writing             |
      | reading and writing |

  @FOG_KERNEL_033
  Scenario Outline: A ram file opens only for the access its permissions give, as lib9p's hasperm decides
    Given "/tmp/p" holds "x" with the permissions <permissions>
    When the process opens "/tmp/p" for <access>
    Then <outcome>

    Examples:
      | permissions | access              | outcome                                 |
      | 0400        | reading             | the open succeeds                       |
      | 0040        | reading             | the open succeeds                       |
      | 0004        | reading             | the open succeeds                       |
      | 0200        | writing             | the open succeeds                       |
      | 0600        | reading and writing | the open succeeds                       |
      | 0100        | executing           | the open succeeds                       |
      | 0400        | writing             | the call fails with "permission denied" |
      | 0200        | reading             | the call fails with "permission denied" |
      | 0400        | reading and writing | the call fails with "permission denied" |
      | 0400        | truncating          | the call fails with "permission denied" |
      | 0600        | executing           | the call fails with "permission denied" |
      | 0404        | reading             | the open succeeds                       |
      | 0600        | writing, truncating | the open succeeds                       |
      | 0400        | writing, truncating | the call fails with "permission denied" |

  @FOG_KERNEL_034
  Scenario Outline: A file stats as the device that serves it, as devdir gives it
    When the process stats "<path>"
    Then the entry is from device <type> number 0

    Examples:
      | path      | type |
      | /tmp      | R    |
      | /dev/null | c    |
      | /fd       | d    |
      | /env      | e    |

  @FOG_KERNEL_036
  Scenario Outline: Every call that names a file checks the name first, as namec does
    When the process <call> the name "<name>"
    Then the call fails with the message "<message>"

    Examples:
      | call       | name             | message                                    |
      | opens      |                  | empty file name                            |
      | creates    |                  | empty file name                            |
      | stats      |                  | empty file name                            |
      | removes    |                  | empty file name                            |
      | changes to |                  | empty file name                            |
      | wstats     |                  | empty file name                            |
      | execs      |                  | empty file name                            |
      | opens      | /tmp/a\tb        | bad character in file name: '/tmp/a\tb'    |
      | creates    | /tmp/a\x01b      | bad character in file name: '/tmp/a\x01b'  |
      | stats      | /tmp/a\x7fb      | bad character in file name: /tmp/a\x7fb    |
      | removes    | /tmp/f\0         | bad character in file name: /tmp/f         |
      | changes to | /tmp/a\x0ab      | bad character in file name: '/tmp/a\x0ab'  |
      | wstats     | /tmp/a\x1fb      | bad character in file name: '/tmp/a\x1fb'  |
      | execs      | /tmp/a\tb        | bad character in file name: '/tmp/a\tb'    |
      | unmounts   | /tmp/a\tb        | bad character in file name: '/tmp/a\tb'    |
      | unmounts from | /tmp/a\tb     | bad character in file name: '/tmp/a\tb'    |
      | opens      | /tmp/<65531 a>   | name too long                              |
      | creates    | /tmp/<32766 é>   | name too long                              |
      | stats      | /<32767 é>a      | name too long                              |
      | execs      | /tmp/<65531 a>   | name too long                              |
      | opens      | /tmp/<65531 a>\t | name too long                              |

  @FOG_KERNEL_036
  Scenario Outline: A name just short of 64 KiB of UTF-8, or with a space in it, is a name
    When the process creates the name "<name>"
    Then the open succeeds

    Examples:
      | name           |
      | /tmp/<65530 a> |
      | /tmp/<32765 é> |
      | /tmp/a b       |

  @FOG_KERNEL_037
  Scenario Outline: An element of a path made only of white space is refused, after namec's checks
    When the process <call> the name "<name>"
    Then the call fails with the message "<message>"

    Examples:
      | call       | name                 | message                                    |
      | opens      | \x20                 | blank file name                            |
      | creates    | /tmp/\x20            | blank file name                            |
      | creates    | /tmp/\x20\x20/f      | blank file name                            |
      | stats      | \x20/tmp             | blank file name                            |
      | removes    | /tmp/\u3000          | blank file name                            |
      | changes to | /tmp/\u00a0\u2003    | blank file name                            |
      | wstats     | /\x20                | blank file name                            |
      | execs      | /tmp/\x20            | blank file name                            |
      | opens      | /tmp/\x20\x01        | bad character in file name: '/tmp/ \x01'   |
      | opens      | /tmp/\x20/<65530 a>  | name too long                              |
      | creates    | /tmp/<65531 \x20>    | name too long                              |

  @FOG_KERNEL_037
  Scenario Outline: A name with something besides white space in it is a name
    When the process creates the name "<name>"
    Then the open succeeds

    Examples:
      | name           |
      | /tmp/\x20a     |
      | /tmp/a\x20     |
      | /tmp/\u3000.b  |
      | //tmp//g       |

  @FOG_KERNEL_037
  Scenario Outline: wstat and fwstat refuse a new name made only of white space, of any length
    Given the process has "/tmp/f" open as descriptor 0
    When the process <call> to the name "<name>"
    Then the call fails with the message "<message>"

    Examples:
      | call                   | name         | message         |
      | renames "/tmp/f"       | \x20         | blank file name |
      | renames "/tmp/missing" | \u3000       | blank file name |
      | renames descriptor 0   | \x20\x20     | blank file name |
      | renames "/tmp/f"       | <64 \x20>    | blank file name |
      | renames descriptor 0   | <70 \x20>    | blank file name |

  @FOG_KERNEL_036
  Scenario Outline: The message quotes the name as %q does, within the message's 128 bytes
    When the process opens the name "<name>"
    Then the call fails with the message "<message>"

    Examples:
      | name                    | message                                                  |
      | /tmp/it's\x01           | bad character in file name: '/tmp/it''s\x01'             |
      | /tmp/$\x7f              | bad character in file name: '/tmp/$\x7f'                 |
      | /tmp/<80 a>\x01<40 a>   | bad character in file name: '/tmp/<80 a>\x01<11 a>'      |
      | /tmp/<92 a>\x01         | bad character in file name: /tmp/<92 a>                  |
      | /tmp/<200 a>\x01        | bad character in file name: /tmp/<94 a>                  |
      | /tmp/a b\x7f            | bad character in file name: '/tmp/a b\x7f'               |
      | /tmp/<91 a>\x01         | bad character in file name: '/tmp/<91 a>\x01'            |
      | /tmp/\x01<89 a>'        | bad character in file name: '/tmp/\x01<89 a>'''          |
      | /tmp/\x01<90 a>'        | bad character in file name: '/tmp/\x01<90 a>'            |
      | /tmp/'<200 a>\x01       | bad character in file name: '/tmp/''<90 a>'              |

  @FOG_KERNEL_036
  Scenario Outline: wstat and fwstat check a new name before the file, refusing a slash in it too
    Given the process has "/tmp/f" open as descriptor 0
    When the process <call> to the name "<name>"
    Then the call fails with the message "<message>"

    Examples:
      | call                         | name         | message                                 |
      | renames "/tmp/missing"       | a/b          | bad character in file name: a/b         |
      | renames "/tmp/f"             | a\tb         | bad character in file name: 'a\tb'      |
      | renames ""                   | a/b          | bad character in file name: a/b         |
      | renames descriptor 0         | a/b          | bad character in file name: a/b         |
      | renames descriptor 0         | <61 a>/b     | bad character in file name: <61 a>/b    |

  @FOG_KERNEL_036
  Scenario Outline: A new name that is "/", or of 64 bytes or more, is left to the file server, which ramfs takes
    When the process renames "/tmp/f" to the name "<name>"
    Then listing "/tmp" gives the name "<name>"

    Examples:
      | name     |
      | /        |
      | <62 a>/b |

  @FOG_KERNEL_036
  Scenario Outline: bind resolves its first name before it checks its second
    When the process binds the name "<name>" onto the name "<old>"
    Then the call fails with the message "<message>"

    Examples:
      | name         | old         | message                                  |
      | /tmp/a\tb    | /tmp/f      | bad character in file name: '/tmp/a\tb'  |
      | /tmp/f       | /tmp/a\tb   | bad character in file name: '/tmp/a\tb'  |
      | /tmp/missing | /tmp/a\tb   | file does not exist: '/tmp/missing'      |
      |              | /tmp/f      | empty file name                          |
