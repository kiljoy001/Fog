@fog_kernel_srv
Feature: /srv posts open channels by name, as 9front's devsrv does
  A file created in #s, which the kernel binds on /srv, is posted a process's open channel by
  writing the number of the descriptor that holds it; opening the file then gives the opener that
  same channel, which lives while the file or any opener holds it. A file is created only for
  writing, under a name not in use, and holds one channel; the channel opens only for the mode it
  was opened with, unless that was reading and writing. Its owner or the host owner may remove or
  rename it. Opening clone makes a board, a directory of its own, named by what the opened clone
  reads; when that is closed, the board and what is posted in it are gone.

  Background:
    Given a kernel booted for the host owner "glenda"

  @FOG_KERNEL_073
  Scenario: A channel posted in /srv is the channel its opener is given
    Given the process holds a pipe
    When the process posts the pipe's first end as "/srv/pipe"
    And the process opens "/srv/pipe" for reading and writing
    And the process closes the pipe's first end
    And the process writes "hello" to the opened /srv file
    Then the pipe's second end reads "hello"
    And the names in "/srv" are "clone pipe"

  @FOG_KERNEL_073
  Scenario: A posted channel opens only for the mode it was opened with, unless that was reading and writing
    Given the process has "/dev/null" open for reading
    When the process posts that descriptor as "/srv/null"
    Then opening "/srv/null" for writing fails with "permission denied"
    And opening "/srv/null" for reading succeeds

  @FOG_KERNEL_073
  Scenario Outline: devsrv refuses what it cannot post or open
    Given the process holds a pipe
    When the process posts the pipe's first end as "/srv/taken"
    And the process <does>
    Then the call fails with "<error>"

    Examples:
      | does                                                     | error                         |
      | creates "/srv/taken" for writing                         | file already exists           |
      | creates "/srv/clone" for writing                         | file already exists           |
      | creates "/srv/other" for reading                         | permission denied             |
      | writes its pipe's first end to a new "/srv/twice" twice  | inappropriate use of fd       |
      | writes "99" to a new "/srv/nofd"                         | fd out of range or not open   |
      | writes "000000000000000000000000000000001" to a new "/srv/long" | read or write too large |
      | opens "/srv/taken" with OTRUNC                           | file already exists           |
      | opens "/srv/taken" with ORCLOSE                          | permission denied             |
      | opens a "/srv/empty" it created but never posted         | device shut down              |

  @FOG_KERNEL_073
  Scenario: Only a file's owner or the host owner may remove it, and then it is gone
    Given the process holds a pipe
    When the process posts the pipe's first end as "/srv/gone"
    And a child that becomes "none" removes "/srv/gone"
    Then the child's call fails with "permission denied"
    When the process removes "/srv/gone"
    Then opening "/srv/gone" for reading fails with "file does not exist: '/srv/gone'"

  @FOG_KERNEL_073
  Scenario: wstat renames a posted file and changes its mode, but no name may hold a slash or be clone
    Given the process holds a pipe
    When the process posts the pipe's first end as "/srv/old"
    And the process renames "/srv/old" to "new" and sets its mode to 600
    Then "/srv/new" has the mode 600
    When the process renames "/srv/new" to "a/b"
    Then the call fails with "bad character in file name: a/b"
    When the process renames "/srv/new" to "clone"
    Then the call fails with "file already exists"

  @FOG_KERNEL_073
  Scenario: A file created with ORCLOSE is removed when its creator closes it
    Given the process holds a pipe
    When the process creates "/srv/brief" with ORCLOSE and posts the pipe's first end through it
    Then opening "/srv/brief" for reading fails with "file does not exist: '/srv/brief'"

  @FOG_KERNEL_074
  Scenario: Opening clone makes a board, named by what it reads, whose posts go when it is closed
    Given the process holds a pipe
    When the process opens "/srv/clone" and reads the board's name
    Then "/srv/<board>" is a directory
    When the process posts the pipe's first end as "/srv/<board>/inboard"
    And the process opens "/srv/<board>/inboard" for reading and writing
    Then the opened /srv file is the pipe's first end
    When the process closes the board's clone
    Then opening "/srv/<board>/inboard" for reading fails with "file does not exist: '/srv/<board>'"
