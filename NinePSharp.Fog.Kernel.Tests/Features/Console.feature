@fog_kernel_console
Feature: /dev holds devcons's bintime, null, zero, pid, ppid and user, as 9front binds #c there
  The files belong to the host owner and give everyone the same permissions, which devpermcheck
  applies when they are opened. cons reads what is typed on the host's console, as drawterm's
  devcons reads its standard input when it has no screen: a line at a time, with backspace and ^U
  editing it and ^D ending it, or as it comes once consctl is written rawon, which also makes the
  host's terminal raw until rawoff or the last close of consctl. hostowner reads the host owner,
  who alone may write it, taking the new name along with every process and #s file of the old.
  nvram is the host's, kept in its sealed key store, and the host owner's alone. null reads nothing and takes every write; zero reads as many zero
  bytes as asked; pid and ppid read the reading process's number and its parent's, right-aligned in
  eleven places and followed by a space; user reads the process's user, and only "none" may be
  written to it, which makes the process and the children it forks later none. bintime reads, at
  any offset, as many of four big-endian 8-byte numbers as fit: the nanoseconds since the epoch,
  the clock's ticks, their frequency, and the nanoseconds since the kernel booted.

  Background:
    Given a kernel booted for the host owner "glenda"

  @FOG_KERNEL_013
  Scenario: /dev lists the console files in devcons's order
    When the process lists "/dev"
    Then it sees "bintime cons consctl hostowner null nvram pid ppid sysname user zero capuse caphash"

  @FOG_KERNEL_013
  Scenario: /dev/null reads nothing and takes every write
    Then reading "/dev/null" gives ""
    And writing "abc" to "/dev/null" writes 3 bytes

  @FOG_KERNEL_013
  Scenario: /dev/zero reads as many zero bytes as asked
    Then reading 5 bytes of "/dev/zero" gives 5 zero bytes

  @FOG_KERNEL_014
  Scenario: /dev/pid and /dev/ppid read a child's number and its parent's
    When the process forks a child that reads "/dev/pid" and "/dev/ppid"
    Then the child read its own number and then the parent's, each in twelve bytes

  @FOG_KERNEL_014
  Scenario: /dev/pid is read in pieces from the offset reached
    Then reading "/dev/pid" 5 bytes at a time gives 12 bytes and then nothing

  @FOG_KERNEL_015
  Scenario: /dev/user reads the process's user
    Then reading "/dev/user" gives "glenda"

  @FOG_KERNEL_015
  Scenario: Writing none to /dev/user makes the process and its later children none
    When the process writes "none" to "/dev/user"
    Then reading "/dev/user" gives "none"
    And a child it forks reads "none" from "/dev/user"

  @FOG_KERNEL_014
  Scenario Outline: A process that becomes none keeps only what a file gives others
    Given "/tmp/kept" belongs to the host owner with the permissions <permissions>
    When the process writes "none" to "/dev/user"
    And the process opens "/tmp/kept" for reading
    Then the open <outcome>

    Examples:
      | permissions | outcome                        |
      | 0440        | fails with "permission denied: '/tmp/kept'" |
      | 0444        | succeeds                       |

  @FOG_KERNEL_015
  Scenario: Only none may be written to /dev/user
    When the process writes "glenda" to "/dev/user"
    Then the write fails with "permission denied"

  @FOG_KERNEL_016
  Scenario Outline: The console files are opened with their permissions
    When the process opens "<file>" for writing
    Then the open <outcome>

    Examples:
      | file      | outcome                          |
      | /dev/null | succeeds                         |
      | /dev/user | succeeds                         |
      | /dev/pid  | fails with "permission denied: '/dev/pid'"   |
      | /dev/ppid | fails with "permission denied: '/dev/ppid'"  |
      | /dev/zero | fails with "permission denied: '/dev/zero'"  |

  @FOG_KERNEL_013
  Scenario: A name devcons does not have is not in /dev
    When the process opens "/dev/nonesuch" for writing
    Then the open fails with "file does not exist: '/dev/nonesuch'"

  @FOG_KERNEL_059
  Scenario: /dev/bintime reads the time since the epoch, the clock's ticks and frequency, and the time since boot
    Given the clock reads 1000 seconds since the epoch
    Then reading 32 bytes of "/dev/bintime" gives 1000000000000 nanoseconds, then ticks, their frequency and the nanoseconds since boot

  @FOG_KERNEL_059
  Scenario Outline: /dev/bintime reads as many whole numbers as fit, at any offset
    Given the clock reads 1000 seconds since the epoch
    Then reading <count> bytes of "/dev/bintime" at offset <offset> gives <numbers> numbers

    Examples:
      | count | offset | numbers |
      | 7     | 0      | 0       |
      | 8     | 0      | 1       |
      | 23    | 0      | 2       |
      | 40    | 0      | 4       |
      | 8     | 100    | 1       |

  @FOG_KERNEL_059
  Scenario: /dev/bintime belongs to the host owner, who alone may open it for writing, and its time is not set
    Then "/dev/bintime" belongs to the host owner with the mode 664
    When the process writes "n12345678" to "/dev/bintime"
    Then the write fails with "permission denied"

  @FOG_KERNEL_076
  Scenario Outline: /dev/cons reads a line typed on the host's console, cooked as drawterm cooks it
    Given the host's console is typed "<typed>"
    Then reading "/dev/cons" gives "<line>"

    Examples:
      | typed             | line    |
      | abc\\n            | abc\\n   |
      | ab\bc\\n          | ac\\n    |
      | \b\bx\\n          | x\\n     |
      | ab\x15cd\\n       | cd\\n    |
      | ab\x04            | ab      |
      | \x04              |         |

  @FOG_KERNEL_076
  Scenario: /dev/cons gives no more than a line to each read, and the rest of one too long to the next
    Given the host's console is typed "abcdef\none\n"
    Then reading 3 bytes of "/dev/cons" from one descriptor gives "abc", "def", "\n", "one", "\n"

  @FOG_KERNEL_076
  Scenario: Once the host's input ends, /dev/cons gives what was typed of a line, then nothing
    Given the host's console is typed "ab" and then ends
    Then reading 10 bytes of "/dev/cons" from one descriptor gives "ab", ""

  @FOG_KERNEL_076
  Scenario: A line of 1024 bytes is given without waiting for its end
    Given the host's console is typed 1030 bytes and a newline
    Then reading 2000 bytes of "/dev/cons" from one descriptor gives 1024 bytes and then 7

  @FOG_KERNEL_077
  Scenario: rawon makes /dev/cons give what is typed as it comes, and the host's terminal raw
    Given the host's console is typed "a\bb\x15"
    When the process writes "rawon" to "/dev/consctl"
    Then reading 10 bytes of "/dev/cons" from one descriptor gives "a\bb\x15"
    And the host's terminal is raw

  @FOG_KERNEL_077
  Scenario: A raw read gives first what is left of a cooked line
    Given the host's console is typed "abcdef\nxy"
    Then reading 3 bytes of "/dev/cons" from one descriptor gives "abc"
    When the process writes "rawon" to "/dev/consctl"
    Then reading 10 bytes of "/dev/cons" from one descriptor gives "def\n", "xy"

  @FOG_KERNEL_077
  Scenario: rawoff cooks again, and so does the last close of consctl
    Given the host's console is typed "a\bb\n"
    When the process opens "/dev/consctl" twice and writes "rawon rawoff rawon" to the first
    Then the host's terminal is raw
    When the process closes the first "/dev/consctl"
    Then the host's terminal is raw
    When the process closes the second "/dev/consctl"
    Then the host's terminal is cooked
    And reading "/dev/cons" gives "b\n"

  @FOG_KERNEL_077
  Scenario: consctl is only for writing
    When the process opens "/dev/consctl" for reading
    Then the open fails with "permission denied: '/dev/consctl'"

  @FOG_KERNEL_078
  Scenario: /dev/hostowner reads the host owner
    Then reading "/dev/hostowner" gives "glenda"
    And "/dev/hostowner" belongs to the host owner with the mode 664

  @FOG_KERNEL_078
  Scenario: The host owner names a new host owner, which every process and /srv file of the old one becomes
    Given a child of the host owner that waits
    And the host owner posts "/srv/kept"
    When the process writes "bootes" to "/dev/hostowner"
    Then reading "/dev/hostowner" gives "bootes"
    And reading "/dev/user" gives "bootes"
    And the waiting child's user is "bootes"
    And "/srv/kept" belongs to "bootes"
    And "/dev/cons" belongs to the host owner with the mode 660

  @FOG_KERNEL_078
  Scenario Outline: /dev/hostowner takes a name of at most 27 bytes, ending at a NUL
    When the process writes "<name>" to "/dev/hostowner"
    Then the write <outcome>

    Examples:
      | name                         | outcome                               |
      |                              | fails with "bad arg in system call"   |
      | abcdefghijklmnopqrstuvwxyz01 | fails with "name too long"            |
      | abcdefghijklmnopqrstuvwxyz0  | makes the host owner "abcdefghijklmnopqrstuvwxyz0" |
      | bootes\x00x                  | makes the host owner "bootes"         |

  @FOG_KERNEL_078
  Scenario: Only the host owner may write /dev/hostowner
    When the process writes "none" to "/dev/user"
    And the process opens "/dev/hostowner" for writing
    Then the open fails with "permission denied: '/dev/hostowner'"

  @FOG_KERNEL_079
  Scenario: /dev/nvram is the host's, written as far as it reaches, and the host owner's alone
    Given the host's nvram holds 8 bytes "abcdefgh"
    Then reading "/dev/nvram" gives "abcdefgh"
    And "/dev/nvram" belongs to the host owner with the mode 664
    When the process writes "XYZ" to "/dev/nvram" at offset 6
    Then reading "/dev/nvram" gives "abcdefXY"
    And the host's nvram holds "abcdefXY"
    When the process writes "none" to "/dev/user"
    And the process opens "/dev/nvram" for reading
    Then the open fails with "permission denied: '/dev/nvram'"
