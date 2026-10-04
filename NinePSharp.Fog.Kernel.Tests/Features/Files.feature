@fog_kernel_files
Feature: stat, seek and bind act on files and the namespace as 9front's do
  stat(2) reads a file's directory entry: its name as the path names it, its length and its
  permissions. seek(2) moves a descriptor's offset from the start, from where it is or from the end.
  bind(2) makes a file or directory visible at another name in the process's namespace, replacing
  what is there or joining a union before or after it.

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
      | /    | /    | d0777       |

  @FOG_KERNEL_025
  Scenario: Creating in a file fails, as ramfs refuses a create in a non-directory
    When the process creates "/tmp/f/x" holding "y"
    Then the call fails with "create in non-directory: '/tmp/f/x'"
