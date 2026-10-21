@fog_kernel_console
Feature: /dev holds devcons's bintime, null, zero, pid, ppid and user, as 9front binds #c there
  The files belong to the host owner and give everyone the same permissions, which devpermcheck
  applies when they are opened. null reads nothing and takes every write; zero reads as many zero
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
    Then it sees "bintime cons null pid ppid sysname user zero capuse caphash"

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
