@fog_kernel_status
Feature: pread, pwrite, fstat and wstat act on files as 9front's do
  pread(2) and pwrite(2) read and write at an explicit offset without moving the descriptor's offset;
  an offset of -1 uses and advances it, which is what read and write do. fstat reads the directory
  entry of an open file under the name it was opened by. wstat and fwstat change an entry, leaving
  alone every field set as nulldir sets it: all ones, or an empty string. ramfs, which serves /tmp,
  keeps a file in 64 KiB blocks up to 2^28-1 of them. It renames a file within its directory,
  truncates or extends it, and changes its mode and modification time; it checks a change of group
  but does not make it. It refuses a name already taken, a length on a directory, a length past its
  largest file, and renaming its root. The kernel's env, cons and dup devices refuse every change,
  and a pipe's data files take only a buffer length no larger than a pipe's queue.

  Background:
    Given a booted kernel
    And "/tmp/f" holds "hello"

  @FOG_KERNEL_026
  Scenario Outline: pread and pwrite use an explicit offset, and -1 uses the descriptor's own
    Given the process has "/tmp/f" open as descriptor 0
    When it <does> at offset <offset>
    Then <result>
    And reading descriptor 0 gives "<rest>"

    Examples:
      | does           | offset | result                         | rest  |
      | preads 3 bytes | 1      | the pread gives "ell"          | hello |
      | preads 3 bytes | -1     | the pread gives "hel"          | lo    |
      | pwrites "J"    | 0      | reading "/tmp/f" gives "Jello" | Jello |
      | pwrites "J"    | -1     | reading "/tmp/f" gives "Jello" | ello  |

  @FOG_KERNEL_026
  Scenario Outline: ramfs keeps a file in 64 KiB blocks, reading a hole as zeros, up to its largest file
    Given the process has "/tmp/f" open as descriptor 0
    When it <does> at offset <offset>
    Then <result>

    Examples:
      | does           | offset            | result                                         |
      | pwrites "J"    | 17592185978879    | stating "/tmp/f" gives the length 17592185978880 |
      | pwrites "J"    | 17592185978880    | the call fails with "bad file offset or count"   |
      | pwrites "J"    | 131072            | preading 3 bytes at 131070 gives the bytes 0 0 74 |
      | pwrites "J"    | 131072            | preading 3 bytes at 4 gives the bytes 111 0 0      |
      | preads 3 bytes | 17592185978880    | the pread gives ""                               |
      | pwrites "J"    | 17592185978879    | preading 1 bytes at 17592185978879 gives the bytes 74 |
      | pwrites "J"    | 17592185978879    | preading 2 bytes at 17592185978879 fails with "bad file offset or count" |
      | pwrites "JJ"   | 4                 | reading "/tmp/f" gives "hellJJ"                  |

  @FOG_KERNEL_026
  Scenario Outline: A write across a block boundary splits between the blocks
    Given the process has "/tmp/f" open as descriptor 0
    When it pwrites "ABCD" at offset 65534
    Then preading <count> bytes at <offset> gives the bytes <bytes>

    Examples:
      | count | offset | bytes          |
      | 5     | 65532  | 0 0 65 66 67   |
      | 3     | 3      | 108 111 0      |
      | 3     | 65537  | 68             |

  @FOG_KERNEL_026
  Scenario Outline: Reading or writing nothing leaves a file's times; reading sets its access time, writing both
    Given the process has "/tmp/f" open as descriptor 0
    And the clock reads 2000
    When it <does> at offset <offset>
    Then stating "/tmp/f" gives the access time <atime> and the modification time <mtime>

    Examples:
      | does           | offset | atime | mtime |
      | preads 3 bytes | 5      | 1000  | 1000  |
      | preads 0 bytes | 0      | 1000  | 1000  |
      | pwrites ""     | 0      | 1000  | 1000  |
      | preads 3 bytes | 0      | 2000  | 1000  |
      | pwrites "J"    | 0      | 2000  | 2000  |

  @FOG_KERNEL_027
  Scenario: fstat reads the open file's entry under the name it was opened by
    Given the process has "/tmp/f" open as descriptor 0
    When the process fstats descriptor 0
    Then the entry is named "f", is 5 bytes long and has the permissions 0666

  @FOG_KERNEL_027
  Scenario Outline: fstat or fwstat of a descriptor that is not open fails
    When the process <call>
    Then the call fails with "fd out of range or not open"

    Examples:
      | call                                   |
      | fstats descriptor 7                    |
      | fwstats descriptor 7 with the mode 0600 |

  @FOG_KERNEL_028
  Scenario Outline: wstat changes only the fields it sets
    When the process wstats "/tmp/f" with <change>
    And the process stats "<path>"
    Then the entry is named "<name>", is <length> bytes long and has the permissions <permissions>

    Examples:
      | change        | path   | name | length | permissions |
      | the name "g"  | /tmp/g | g    | 5      | 0666        |
      | the length 2  | /tmp/f | f    | 2      | 0666        |
      | the length 8  | /tmp/f | f    | 8      | 0666        |
      | the mode 0640 | /tmp/f | f    | 5      | 0640        |
      | the length 17592185978880 | /tmp/f | f | 17592185978880 | 0666 |
      | nothing       | /tmp/f | f    | 5      | 0666        |

  @FOG_KERNEL_028
  Scenario: Truncating keeps the start of a file, and wstat sets its modification time
    When the process wstats "/tmp/f" with the length 2
    And the process wstats "/tmp/f" with the modification time 1234
    Then reading "/tmp/f" gives "he"
    And stating "/tmp/f" gives the modification time 1234

  @FOG_KERNEL_028
  Scenario Outline: A shorter length drops the blocks past it, and extending reads zeros where they were
    Given the process has "/tmp/f" open as descriptor 0
    And it pwrites "ABCD" at offset 65534
    When the process wstats "/tmp/f" with the length <cut>
    And the process wstats "/tmp/f" with the length 65538
    Then preading <count> bytes at <offset> gives the bytes <bytes>

    Examples:
      | cut   | count | offset | bytes    |
      | 2     | 3     | 1      | 101 0 0  |
      | 65537 | 2     | 65536  | 67 0     |
      | 65537 | 1     | 0      | 104      |
      | 65536 | 2     | 65536  | 0 0      |
      | 1     | 2     | 65536  | 0 0      |

  @FOG_KERNEL_028
  Scenario: A file cut to nothing reads as empty until written, whatever its length, as ramfs's does
    When the process wstats "/tmp/f" with the length 0
    And the process wstats "/tmp/f" with the length 5
    Then reading "/tmp/f" gives ""
    And stating "/tmp/f" gives the length 5

  @FOG_KERNEL_028
  Scenario: fwstat changes the file a descriptor names
    Given the process has "/tmp/f" open as descriptor 0
    When the process fwstats descriptor 0 with the name "g"
    Then reading "/tmp/g" gives "hello"

  @FOG_KERNEL_028
  Scenario Outline: wstat refuses what ramfs refuses
    Given "/tmp/g" holds "other"
    When the process wstats "<path>" with <change>
    Then the call fails with "<error>"

    Examples:
      | path   | change                          | error                    |
      | /tmp/f | the name "g"                    | file already exists      |
      | /tmp   | the length 4                    | permission denied        |
      | /tmp/f | the length 1152921504606846976  | bad file offset or count |
      | /      | the name "root"                 | permission denied        |

  @FOG_KERNEL_028
  Scenario: ramfs checks a change of group but leaves the group as it was
    When the process wstats "/tmp/f" with the group "sys"
    Then stating "/tmp/f" gives the group "none"

  @FOG_KERNEL_028
  Scenario Outline: Only the owner or the group may change the mode or the group
    Given the process runs as "glenda"
    When the process wstats "/tmp/f" with <change>
    Then the call fails with "not owner"

    Examples:
      | change          |
      | the mode 0600   |
      | the group "sys" |

  @FOG_KERNEL_028
  Scenario: A file's group, the group of the directory it was made in, may change its mode
    Given "glenda" creates "/tmp/g" holding "" with the mode 0644
    When the process wstats "/tmp/g" with the mode 0600
    And the process stats "/tmp/g"
    Then the entry is named "g", is 0 bytes long and has the permissions 0600

  @FOG_KERNEL_028
  Scenario Outline: Anyone may rename a file in a directory they can write, or set the mode or group it has
    Given the process runs as "glenda"
    When the process wstats "/tmp/f" with <change>
    And the process stats "<path>"
    Then the entry is named "<name>", is 5 bytes long and has the permissions 0666

    Examples:
      | change           | path   | name |
      | the name "h"     | /tmp/h | h    |
      | the mode 0666    | /tmp/f | f    |
      | the group "none" | /tmp/f | f    |

  @FOG_KERNEL_028
  Scenario Outline: Changing a file's length needs permission to write it as its owner, its group or anyone
    Given "<owner>" creates "/tmp/g" holding "hello" with the mode <mode>
    And the process runs as "<user>"
    When the process wstats "/tmp/g" with the length 1
    Then stating "/tmp/g" gives the length <length>

    Examples:
      | owner  | mode | user   | length |
      | none   | 0640 | glenda | 5      |
      | none   | 0602 | glenda | 1      |
      | none   | 0600 | glenda | 5      |
      | glenda | 0260 | none   | 1      |
      | glenda | 0206 | none   | 1      |
      | glenda | 0600 | none   | 5      |
      | glenda | 0200 | glenda | 1      |
      | glenda | 0400 | glenda | 5      |

  @FOG_KERNEL_028
  Scenario: wstat of a file that does not exist names the missing file
    When the process wstats "/tmp/missing" with the mode 0600
    Then the call fails with "file does not exist: '/tmp/missing'"

  @FOG_KERNEL_029
  Scenario Outline: The kernel's devices refuse wstat
    Given "/env/x" holds "1"
    When the process wstats "<path>" with the mode 0600
    Then the call fails with "permission denied"

    Examples:
      | path      |
      | /env      |
      | /env/x    |
      | /dev/zero |
      | /dev/null |
      | /fd       |

  @FOG_KERNEL_030
  Scenario Outline: fwstat of either end of a pipe sets how much each end holds before a writer waits
    When the process makes a pipe
    And the process fwstats the pipe's first end with the length 16
    And it starts writing 32 bytes to the <writer> end
    Then the write has not finished
    When it reads 32 bytes from the <reader> end
    Then the write finishes

    Examples:
      | writer | reader |
      | first  | second |
      | second | first  |

  @FOG_KERNEL_030
  Scenario: A larger length lets a waiting writer go on
    When the process makes a pipe
    And the process fwstats the pipe's first end with the length 16
    And it starts writing 32 bytes to the first end
    And the process fwstats the pipe's first end with the length 64
    Then the write finishes

  @FOG_KERNEL_030
  Scenario Outline: fwstat of a pipe takes a length up to a pipe's queue
    When the process makes a pipe
    And the process fwstats the pipe's first end with the length <length>
    Then the call <result>

    Examples:
      | length | result                              |
      | 262144 | succeeds                            |
      | 262145 | fails with "bad arg in system call" |

  @FOG_KERNEL_030
  Scenario Outline: fstat of a pipe end reports what is queued for it to read
    When the process makes a pipe
    And it writes "abcde" to the first end
    And it reads 2 bytes from the second end
    And the process fstats the pipe's <end> end
    Then the entry is named "<name>", is <length> bytes long and has the permissions 0666

    Examples:
      | end    | name  | length |
      | first  | data  | 0      |
      | second | data1 | 3      |
