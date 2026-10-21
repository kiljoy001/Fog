@fog_kernel_cap
Feature: #¤ lets a process that knows a capability become the user it names, as 9front's devcap does
  The device #¤, which the kernel binds after /dev's files, holds capuse and caphash. Only the host owner may
  open or write caphash, and what it writes, the HMAC-SHA1 of from@to keyed by a secret, is a
  capability; the first 20 bytes are kept, as many as SHA1 gives. Writing from@to@key, or to@key, to
  capuse spends the capability whose hash that names and makes the writer to; from, when given,
  must be the writer's user. A capability is spent once and kept for 60 seconds, and at most 256
  are kept: the oldest is let go to make room for another, or as one is looked for among 256.
  Neither file can be read. The host owner may remove caphash,
  after which no one can make a capability. As factotum makes one for auth_chuid to spend.

  Background:
    Given a kernel booted for the host owner "glenda"

  @FOG_KERNEL_075
  Scenario: #¤ holds capuse and caphash, which the kernel binds in /dev
    Then the names in "#¤" are "capuse caphash"
    And the names in "/dev" include "capuse" and "caphash"
    And "#¤" belongs to "glenda" with the mode d500
    And "#¤/capuse" belongs to "glenda" with the mode 222
    And "#¤/caphash" belongs to "glenda" with the mode 200

  @FOG_KERNEL_075
  Scenario Outline: Spending a capability makes the writer the user it names
    Given the host owner has made the capability "<capability>" keyed by "secret"
    When a child that becomes "<writer>" writes "<use>" to "/dev/capuse"
    Then the child's write succeeds
    And the child's user is "<user>"

    Examples:
      | capability  | writer | use                | user  |
      | glenda@alice | glenda | glenda@alice@secret | alice |
      | alice        | none   | alice@secret        | alice |
      | alice        | none   | alice@secret\0junk  | alice |

  @FOG_KERNEL_075
  Scenario: A capability is spent once
    Given the host owner has made the capability "alice" keyed by "secret"
    When a child that becomes "none" writes "alice@secret" to "/dev/capuse"
    And a child that becomes "none" writes "alice@secret" to "/dev/capuse"
    Then the child's write fails with "invalid capability alice@secret"
    And the child's user is "none"

  @FOG_KERNEL_075
  Scenario Outline: capuse refuses what names no capability, or one for another user
    Given the host owner has made the capability "bob@alice" keyed by "secret"
    When a child that becomes "none" writes "<use>" to "/dev/capuse"
    Then the child's write fails with "<error>"
    And the child's user is "none"

    Examples:
      | use               | error                            |
      | bob@alice@secret  | capability must match user       |
      | none@alice@secret | invalid capability none@secret   |
      | alice@other       | invalid capability alice@other   |
      | alice             | i/o count too small              |
      | \0alice@secret    | i/o count too small              |
      | @secret           | invalid capability @secret       |
      | @alice@secret     | capability must match user       |
      | <1022 a>@k        | read or write too large          |

  @FOG_KERNEL_075
  Scenario Outline: A capability is kept for 60 seconds
    Given the host owner has made the capability "alice" keyed by "secret"
    When <seconds> seconds pass
    And a child that becomes "none" writes "alice@secret" to "/dev/capuse"
    Then the child's write <outcome>

    Examples:
      | seconds | outcome                                       |
      | 59      | succeeds                                      |
      | 60      | fails with "invalid capability alice@secret"  |

  @FOG_KERNEL_075
  Scenario: At most 256 capabilities are kept, and the oldest goes first, also as one is looked for among 256
    Given the host owner has made the capabilities "u0" to "u256" keyed by "secret"
    When a child that becomes "none" writes "u0@secret" to "/dev/capuse"
    Then the child's write fails with "invalid capability u0@secret"
    When a child that becomes "none" writes "u1@secret" to "/dev/capuse"
    Then the child's write fails with "invalid capability u1@secret"
    When a child that becomes "none" writes "u2@secret" to "/dev/capuse"
    Then the child's write succeeds

  @FOG_KERNEL_075
  Scenario: The host owner who opened caphash, once none, may not write it
    When the process opens "/dev/caphash" for writing, becomes "none" and writes a hash to it
    Then the call fails with "permission denied"

  @FOG_KERNEL_075
  Scenario: Only the host owner may open caphash
    When a child that becomes "none" opens "/dev/caphash" for writing
    Then the child's open fails with "permission denied: '/dev/caphash'"

  @FOG_KERNEL_075
  Scenario Outline: Neither file can be read, and a capability is a whole hash
    When the process <does>
    Then the call fails with "<error>"

    Examples:
      | does                                   | error                 |
      | reads "/dev/capuse"                    | permission denied     |
      | reads "/dev/caphash"                   | permission denied     |
      | writes 19 bytes to "/dev/caphash"      | i/o count too small   |
      | opens "#¤" for writing                 | bad arg in system call |

  @FOG_KERNEL_075
  Scenario: The host owner may remove caphash, and then no capability can be made
    When the process removes "#¤/caphash"
    Then the names in "#¤" are "capuse"
    And opening "/dev/caphash" for writing fails with "file does not exist: '/dev/caphash'"

  @FOG_KERNEL_075
  Scenario Outline: No one else may remove caphash, and no one may remove capuse or #¤
    When <who> removes "<path>"
    Then the call fails with "permission denied"

    Examples:
      | who                         | path        |
      | a child that becomes "none" | #¤/caphash  |
      | the process                 | #¤/capuse   |
      | the process                 | #¤          |
