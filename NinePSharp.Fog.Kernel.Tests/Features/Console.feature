@fog_kernel_console
Feature: /dev holds devcons's null, zero, pid, ppid and user, as 9front binds #c there
  The files belong to the host owner and give everyone the same permissions, which devpermcheck
  applies when they are opened. null reads nothing and takes every write; zero reads as many zero
  bytes as asked; pid and ppid read the reading process's number and its parent's, right-aligned in
  eleven places and followed by a space; user reads the process's user, and only "none" may be
  written to it, which makes the process and the children it forks later none.

  Background:
    Given a kernel booted for the host owner "glenda"

  @FOG_KERNEL_013
  Scenario: /dev lists the console files in devcons's order
    When the process lists "/dev"
    Then it sees "null pid ppid user zero"

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
      | /dev/pid  | fails with "permission denied"   |
      | /dev/ppid | fails with "permission denied"   |
      | /dev/zero | fails with "permission denied"   |

  @FOG_KERNEL_013
  Scenario: A name devcons does not have is not in /dev
    When the process opens "/dev/nonesuch" for writing
    Then the open fails with "file does not exist: '/dev/nonesuch'"
