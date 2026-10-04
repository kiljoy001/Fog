@fog_kernel_descriptors
Feature: /fd names a process's open descriptors as 9front's devdup does
  /fd holds a file N and a file Nctl for each open descriptor N of the process that looks. Opening
  /fd/N gives a new descriptor on the same channel as descriptor N, sharing its offset, in a mode
  the descriptor allows: any mode when it was opened for reading and writing, otherwise the same
  mode, with OEXEC counting as reading. ORCLOSE is refused.

  Background:
    Given a booted kernel
    And "/tmp/f" holds "hello"

  @FOG_KERNEL_011
  Scenario: Opening /fd/N continues from where descriptor N is
    Given a process with "/tmp/f" open for reading as descriptor 0
    And it has read 2 bytes from descriptor 0
    When it opens "/fd/0" for reading
    Then the new descriptor is 1
    And reading descriptor 1 gives "llo"

  @FOG_KERNEL_011
  Scenario: A descriptor opened through /fd shares a pipe's end
    Given a process with a pipe
    When it opens "/fd/1" for reading and writing
    And it writes "hi" to descriptor 0
    Then the new descriptor is 2
    And reading the new descriptor gives "hi"

  @FOG_KERNEL_011
  Scenario: /fd lists each open descriptor and its ctl file, the one reading /fd included
    Given a process with "/tmp/f" open for reading as descriptor 0
    When it lists "/fd"
    Then it sees "0 0ctl 1 1ctl"

  @FOG_KERNEL_011
  Scenario Outline: A descriptor's file in /fd has the permissions of its open mode, and its ctl file is read-only
    Given a process with "/tmp/f" open for <mode> as descriptor 0
    When it lists "/fd"
    Then it sees the permissions "<permissions> 0400 0400 0400"

    Examples:
      | mode                | permissions |
      | reading             | 0400        |
      | writing             | 0200        |
      | reading and writing | 0600        |
      | execution           | 0400        |

  @FOG_KERNEL_012
  Scenario Outline: Opening /fd/N in a mode descriptor N was not opened with fails
    Given a process with "/tmp/f" open for <opened> as descriptor 0
    When it opens "/fd/0" for <mode>
    Then the open fails with "<error>"

    Examples:
      | opened                | mode                  | error                       |
      | reading               | writing               | inappropriate use of fd     |
      | writing               | reading               | inappropriate use of fd     |
      | reading               | reading and writing   | inappropriate use of fd     |
      | reading               | reading, removed on close | permission denied       |

  @FOG_KERNEL_012
  Scenario Outline: Opening /fd/N in a mode descriptor N allows succeeds
    Given a process with "/tmp/f" open for <opened> as descriptor 0
    When it opens "/fd/0" for <mode>
    Then the new descriptor is 1

    Examples:
      | opened              | mode                |
      | reading and writing | writing             |
      | reading             | execution           |
      | writing             | writing, truncating |

  @FOG_KERNEL_012
  Scenario Outline: Only open descriptors have names in /fd
    Given a process with "/tmp/f" open for reading as descriptor 0
    When it opens "<path>" for reading
    Then the open fails with "file does not exist: '<path>'"

    Examples:
      | path   |
      | /fd/1  |
      | /fd/00 |
      | /fd/x  |

  @FOG_KERNEL_023
  Scenario: Creating /fd/N opens descriptor N's channel, as creating a file that exists opens it
    Given a process with a pipe
    When it creates "/fd/1" for writing
    And it writes "hi" to descriptor 2
    Then the new descriptor is 2
    And reading descriptor 0 gives "hi"

  @FOG_KERNEL_023
  Scenario: Creating /fd/N in a mode descriptor N was not opened with fails
    Given a process with "/tmp/f" open for reading as descriptor 0
    When it creates "/fd/0" for writing
    Then the open fails with "inappropriate use of fd"

  @FOG_KERNEL_024
  Scenario Outline: Reading or writing a descriptor in a mode it was not opened with fails, as fdtochan refuses it
    Given a process with "/tmp/f" open for <opened> as descriptor 0
    When it <call> descriptor 0
    Then the call fails with "inappropriate use of fd"

    Examples:
      | opened  | call      |
      | writing | reads     |
      | reading | writes to |

  @FOG_KERNEL_024
  Scenario: A descriptor opened for execution can be read
    Given a process with "/tmp/f" open for execution as descriptor 0
    Then reading descriptor 0 gives "hello"
