@fog_kernel_srv
Feature: /srv posts open channels by name, as 9front's devsrv does
  A file created in #s, which the kernel binds on /srv, is posted a process's open channel by
  writing the number of the descriptor that holds it; opening the file then gives the opener that
  same channel, which lives while the file or any opener holds it. A file is created only for
  writing, under a name not in use, and holds one channel; the channel opens only for the mode it
  was opened with, unless that was reading and writing. Its owner or the host owner may remove or
  rename it. Opening clone makes a board, a directory of its own, named by what the opened clone
  reads, its lease; when that is closed, what is posted in the board is gone, and the board too
  once nothing holds it: its lease, an open of it or of a file made in it, or a board in it. Fog's
  walks hold nothing, so a board reached only by a walk, as a bind holds it, goes all the same.

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
    Then opening "/srv/null" for writing fails with "permission denied: '/srv/null'"
    And opening "/srv/null" for reading succeeds

  @FOG_KERNEL_073
  Scenario Outline: devsrv refuses what it cannot post or open
    Given the process holds a pipe
    When the process posts the pipe's first end as "/srv/taken"
    And the process <does>
    Then the call fails with "<error>"

    Examples:
      | does                                                     | error                         |
      | creates "/srv/taken" for writing                         | file already exists: '/srv/taken' |
      | creates "/srv/clone" for writing                         | file already exists: '/srv/clone' |
      | creates "/srv/other" for reading                         | permission denied: '/srv/other'   |
      | writes its pipe's first end to a new "/srv/twice" twice  | inappropriate use of fd       |
      | writes "99" to a new "/srv/nofd"                         | fd out of range or not open   |
      | writes "000000000000000000000000000000001" to a new "/srv/long" | read or write too large |
      | opens "/srv/taken" with OTRUNC                           | file already exists: '/srv/taken' |
      | opens "/srv/taken" with ORCLOSE                          | permission denied: '/srv/taken'   |
      | opens a "/srv/empty" it created but never posted         | device shut down: '/srv/empty'    |

  @FOG_KERNEL_073
  Scenario: A board opens only for reading, refused as devsrv's open refuses it
    Then opening "/srv" for writing fails with "file is a directory: '/srv'"
    When the process opens "/srv" with OTRUNC
    Then the call fails with "file already exists: '/srv'"

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

  @FOG_KERNEL_074
  Scenario: Files and boards are numbered in the order they are made, from 1
    Given the process holds a pipe
    When the process posts the pipe's first end as "/srv/a"
    And the process posts the pipe's first end as "/srv/b"
    And the process opens "/srv/clone" as the lease "A"
    Then the lease "A" is named "3"
    And the lease "A" read from offset 1 gives ""

  @FOG_KERNEL_074
  Scenario: A lease is no file of its board's, so it does not stat
    When the process opens "/srv/clone" as the lease "A"
    And the process stats the lease "A"
    Then the call fails with "file does not exist"

  @FOG_KERNEL_074
  Scenario: A board held open stays after its lease is closed, and goes when it is let go
    When the process opens "/srv/clone" as the lease "A"
    And the process opens "/srv/<A>" for reading and keeps it as "board"
    And the process closes the lease "A"
    Then the names in what "board" holds are "clone"
    And the names in "/srv/<A>" are "clone"
    When the process closes "board"
    Then opening "/srv/<A>" for reading fails with "file does not exist: '/srv/<A>'"

  @FOG_KERNEL_074
  Scenario: A board whose lease is closed loses what is posted in it, makes nothing more, and stays while a board in it does
    Given the process holds a pipe
    When the process opens "/srv/clone" as the lease "A"
    And the process posts the pipe's first end as "/srv/<A>/posted"
    And the process opens "/srv/<A>/clone" as the lease "B"
    And the process closes the lease "A"
    Then the names in "/srv/<A>" are "<B> clone"
    When the process tries to create "/srv/<A>/x"
    Then the call fails with "device shut down: '/srv/<A>/x'"
    When the process tries to create "/srv/<A>/clone"
    Then the call fails with "file already exists: '/srv/<A>/clone'"
    When the process opens "/srv/<A>/clone" for reading
    Then the call fails with "device shut down: '/srv/<A>/clone'"
    When the process closes the lease "B"
    Then opening "/srv/<A>" for reading fails with "file does not exist: '/srv/<A>'"

  @FOG_KERNEL_074
  Scenario: A file made in a board whose lease is then closed is shut down with it
    Given the process holds a pipe
    When the process opens "/srv/clone" as the lease "A"
    And the process creates "/srv/<A>/x" and keeps it as "x"
    And the process creates "/srv/<A>/y" with ORCLOSE and keeps it as "y"
    And the process closes the lease "A"
    Then the names in "/srv" are "<A> clone"
    When the process writes its pipe's first end to "x"
    Then the call fails with "device shut down"
    When the process renames what "x" holds to "z"
    Then the call fails with "device shut down"
    When the process closes "y"
    Then the call succeeds
    And the names in "/srv" are "<A> clone"
    When the process closes "x"
    Then the names in "/srv" are "clone"

  @FOG_KERNEL_073
  Scenario: A file removed while its maker holds it open takes no channel and has no stat
    Given the process holds a pipe
    When the process creates "/srv/x" and keeps it as "x"
    And the process removes "/srv/x"
    And the process writes its pipe's first end to "x"
    Then the call fails with "file does not exist"
    When the process stats what "x" holds
    Then the call fails with "file does not exist"
    When the process renames what "x" holds to "z"
    Then the call fails with "file does not exist"

  @FOG_KERNEL_073
  Scenario Outline: A name is shorter than genbuf, 128 bytes
    When the process tries to create "/srv/<name>"
    Then the call <outcome>

    Examples:
      | name      | outcome                                                       |
      | <127 a>   | succeeds                                                      |
      | <128 a>   | fails with "name too long: '...<32 a>'"                       |

  @FOG_KERNEL_073
  Scenario Outline: devpermcheck gives a file's owner its owner's bits, the host owner the group's and anyone else the others'
    Given the process holds a pipe
    When <poster> posts the pipe's first end as "/srv/p" with the mode <mode>
    And <opener> opens "/srv/p" for reading
    Then the call <outcome>

    Examples:
      | poster                      | mode | opener                      | outcome                                     |
      | the process                 | 0400 | the process                 | succeeds                                    |
      | the process                 | 0400 | a child that becomes "none" | fails with "permission denied: '/srv/p'"    |
      | the process                 | 0404 | a child that becomes "none" | succeeds                                    |
      | a child that becomes "none" | 0600 | the process                 | fails with "permission denied: '/srv/p'"    |
      | a child that becomes "none" | 0040 | the process                 | succeeds                                    |
      | a child that becomes "none" | 0040 | a child that becomes "none" | fails with "permission denied: '/srv/p'"    |

  @FOG_KERNEL_074
  Scenario: Only a board's owner, or the host owner, may make a file in it
    When the process opens "/srv/clone" as the lease "A"
    And a child that becomes "none" creates "/srv/<A>/x"
    Then the call fails with "permission denied: '/srv/<A>/x'"

  @FOG_KERNEL_073
  Scenario Outline: Only a posted file may be removed
    When the process opens "/srv/clone" as the lease "A"
    And the process tries to remove "<path>"
    Then the call fails with "permission denied"

    Examples:
      | path       |
      | /srv/<A>   |
      | /srv/clone |
      | /srv       |

  @FOG_KERNEL_073
  Scenario: The host owner may rename a file another user posted
    Given the process holds a pipe
    When a child that becomes "none" posts the pipe's first end as "/srv/p" with the mode 0666
    And the process renames "/srv/p" to "q"
    Then the call succeeds
    And the names in "/srv" are "clone q"

  @FOG_KERNEL_073
  Scenario Outline: A posted channel is let go when its file is removed, or when its board's lease is closed
    Given the process holds a pipe
    When the process opens "/srv/clone" as the lease "A"
    And the process posts the pipe's first end as "<file>"
    And the process closes the pipe's first end
    And the process <ends it>
    Then the pipe's second end reads ""

    Examples:
      | file           | ends it                |
      | /srv/p         | removes "/srv/p"       |
      | /srv/<A>/p     | closes the lease "A"   |

  @FOG_KERNEL_074
  Scenario: Only a board's owner, or the host owner, may open it
    When the process opens "/srv/clone" as the lease "A"
    And a child that becomes "none" opens "/srv/<A>" for reading
    Then the call fails with "permission denied: '/srv/<A>'"

  @FOG_KERNEL_074
  Scenario: A board reached only by a walk, as a bind holds it, is gone once its lease is closed
    Given the process holds a pipe
    When the process opens "/srv/clone" as the lease "A"
    And the process posts the pipe's first end as "/srv/<A>/p"
    And the process binds "/srv/<A>" on "/n", allowing creation
    And the process binds "/srv/<A>" after "/tmp"
    And the process binds "/srv/<A>/p" on "/env/p"
    And the process closes the lease "A"
    And the process opens "/n" for reading
    Then the call fails with "device shut down: '/n'"
    When the process tries to create "/n/x"
    Then the call fails with "device shut down: '/n/x'"
    When the process lists "/tmp"
    Then the call fails with "device shut down"
    When the process stats "/env/p"
    Then the call fails with "file does not exist"
    When the process sets the mode of "/env/p" to 600
    Then the call fails with "file does not exist"

  @FOG_KERNEL_073
  Scenario: The host owner may remove a file another user posted
    Given the process holds a pipe
    When a child that becomes "none" posts the pipe's first end as "/srv/p" with the mode 0666
    And the process removes "/srv/p"
    Then the names in "/srv" are "clone"

  @FOG_KERNEL_073
  Scenario Outline: wstat changes neither the root, clone, nor what another owns, and no name may be another's
    Given the process holds a pipe
    When the process posts the pipe's first end as "/srv/a"
    And the process opens "/srv/clone" as the lease "A"
    And <who> renames "<path>" to "<name>"
    Then the call fails with "<error>"

    Examples:
      | who                         | path       | name    | error               |
      | the process                 | #s         | x       | permission denied   |
      | the process                 | /srv/clone | x       | permission denied   |
      | a child that becomes "none" | /srv/a     | x       | permission denied   |
      | the process                 | /srv/a     | <A>     | file already exists |
      | the process                 | /srv/a     | <128 a> | name too long       |
      | the process                 | /srv/<A>   | a       | file already exists |

  @FOG_KERNEL_073
  Scenario: A file renamed to its own name keeps it, and wstat gives a file or a board another owner
    Given the process holds a pipe
    When the process posts the pipe's first end as "/srv/a"
    And the process opens "/srv/clone" as the lease "A"
    And the process tries to rename "/srv/a" to "a"
    Then the call succeeds
    And "/srv/a" belongs to "glenda" with the mode 666
    When the process gives "/srv/a" to "none"
    And the process tries to rename "/srv/<A>" to "renamed"
    Then the call succeeds
    And "/srv/a" belongs to "none" with the mode 666
    And the names in "/srv" are "a clone renamed"
