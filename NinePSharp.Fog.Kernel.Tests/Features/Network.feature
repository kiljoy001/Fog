@fog_kernel_network
Feature: /net/tcp is ip(3)'s file interface over the host's sockets, as 9front binds #I on /net
  The device #I serves tcp as devip and tcp.c do, over the host's sockets as drawterm's devip does. Opening
  /net/tcp/clone reserves a conversation and opens its ctl, which reads the conversation's number.
  Writing "connect addr!port" to a ctl calls out, "announce [addr!]port" takes calls, "bind" sets
  the local port and "hangup" ends the conversation; opening an announced conversation's listen
  waits for a call and opens the new conversation's ctl. data carries the conversation's bytes, and
  local and remote read each end as addr!port. A conversation's ctl, data and listen belong to the
  user who opened it, with the mode 660, so only that user and the host owner may open them, as
  devip's ipopen has it, though devip compares the owner with #I's attacher and Fog with the
  process's user; local, remote and status may be read by anyone. When the last of its ctl and
  data closes, its socket closes and it is free, with the mode 666, for the next clone or open. A
  connect without a local address leaves the host to choose one, whatever was bound before, as
  Fsstdconnect does. A note interrupts a process waiting in listen, connect or a read of data.
  Here the conversations run over the host's loopback.

  Background:
    Given a kernel booted for the host owner "glenda"

  @FOG_KERNEL_060
  Scenario: /net holds tcp, whose directory holds clone and a directory for each conversation
    Then the names in "/net" are "tcp"
    And the names in "/net/tcp" are "clone"
    When the process opens "/net/tcp/clone"
    Then the names in "/net/tcp" are "0 clone"
    And the names in "/net/tcp/0" are "ctl data listen local remote status"

  @FOG_KERNEL_061
  Scenario: Each clone reserves the lowest free conversation, and its ctl reads the number
    When the process opens "/net/tcp/clone" as "a"
    And the process opens "/net/tcp/clone" as "b"
    Then reading "a" gives "0"
    And reading "b" gives "1"
    When the process closes "a"
    And the process opens "/net/tcp/clone" as "c"
    Then reading "c" gives "0"
    When the process opens "/net/tcp/clone" as "d"
    And the process closes "c"
    And the process closes "b"
    And the process opens "/net/tcp/clone" as "e"
    Then reading "e" gives "0"

  @FOG_KERNEL_061
  Scenario: The network makes at most 4095 conversations, as many as a qid's conversation bits hold
    When the process opens "/net/tcp/clone" 4095 times
    And the process opens "/net/tcp/clone"
    Then the call fails with "no free devices: '/net/tcp/clone'"

  @FOG_KERNEL_060
  Scenario Outline: A walk finds only the conversations there are, by the numbers devip gives them
    When the process opens "/net/tcp/clone" as "a"
    And the process stats "<path>"
    Then the call fails with "<error>"

    Examples:
      | path                 | error                                       |
      | /net/tcp/1           | file does not exist: '/net/tcp/1'           |
      | /net/tcp/00          | file does not exist: '/net/tcp/00'          |
      | /net/tcp/0/nonesuch  | file does not exist: '/net/tcp/0/nonesuch'  |

  @FOG_KERNEL_060
  Scenario: Each of #I's files stats under its own name
    When the process opens "/net/tcp/clone" as "a"
    Then the stats of "#I", "/net/tcp", "/net/tcp/clone" and "/net/tcp/0" are named "#I tcp clone 0"
    And the stats of the files in "/net/tcp/0" are named "ctl data listen local remote status"

  @FOG_KERNEL_062
  Scenario: An announced conversation takes a call, and each end reads what the other writes
    Given a conversation announced on "127.0.0.1!0"
    When a child connects to the announced port and writes "hello"
    And the process opens the announced conversation's listen
    Then the call's data reads "hello"
    And the call's remote is the child's local
    And writing "back" to the call's data is read by the child

  @FOG_KERNEL_062
  Scenario: An announced conversation's local names its address and the port the host gave it
    Given a conversation announced on "127.0.0.1!0"
    Then its local is "127.0.0.1" and a port above 0
    And its status begins "Listen"

  @FOG_KERNEL_062
  Scenario Outline: A conversation announced on a port alone, or on *, listens on every address
    Given a conversation announced on "<address>"
    Then its local is "::" and a port above 0

    Examples:
      | address |
      | 0       |
      | *!0     |

  @FOG_KERNEL_062
  Scenario: A call to a conversation announced on every address over v4 names both ends as v4 addresses
    Given a conversation announced on "0"
    When a child connects to "127.0.0.1" at the announced port and writes "hello"
    And the process opens the announced conversation's listen
    Then the call's local is "127.0.0.1" and the announced port
    And the call's remote is the child's local

  @FOG_KERNEL_062
  Scenario: A call over v6 names both ends as v6 addresses
    Given a conversation announced on "::1!0"
    When a child connects to "::1" at the announced port and writes "hello"
    And the process opens the announced conversation's listen
    Then the call's local is "::1" and the announced port
    And the call's remote is the child's local

  @FOG_KERNEL_062
  Scenario: A conversation announced on one address takes no call to another
    Given a conversation announced on "127.0.0.1!0"
    Then a call from the host to "127.0.0.2" at the announced port is refused
    And a call from the host to "127.0.0.1" at the announced port is taken

  @FOG_KERNEL_062
  Scenario: A conversation's status counts the bytes waiting to be read, and reads nothing past its end
    Given a conversation announced on "127.0.0.1!0"
    When a child connects to the announced port and writes "hello"
    And the process opens the announced conversation's listen
    Then the status of the call's conversation becomes "Established qin 5 qout 0\n"
    And reading the call's local from offset 100 gives nothing

  @FOG_KERNEL_062
  Scenario Outline: connect calls from the local address given, or from the one the host chooses
    Given a conversation announced on "127.0.0.1!0"
    When the process opens "/net/tcp/clone" as "a"
    And the process writes "<bind>" to "a"
    And the process writes "<connect>" to "a"
    Then the local of "a" is "<local>"

    Examples:
      | bind              | connect                                  | local              |
      | bind 127.0.0.2!0  | connect 127.0.0.1!$port 127.0.0.2!0      | 127.0.0.2!*        |
      | bind 127.0.0.2!0  | connect 127.0.0.1!$port $free            | 127.0.0.1!$free    |
      | bind 127.0.0.2!0  | connect 127.0.0.1!$port                  | 127.0.0.1!*        |
      | bind 127.0.0.2!0  | connect 127.0.0.1!$port!r                | 127.0.0.1!*        |

  @FOG_KERNEL_062
  Scenario: connect over v6 from a port alone calls from the host's v6 address
    Given a conversation announced on "::1!0"
    When the process opens "/net/tcp/clone" as "a"
    And the process writes "connect ::1!$port $free" to "a"
    Then the local of "a" is "::1!$free"

  @FOG_KERNEL_062
  Scenario Outline: bind sets the local address and port, any address when it gives none
    When the process opens "/net/tcp/clone" as "a"
    And the process writes "bind 127.0.0.2!5" to "a"
    And the process writes "<bind>" to "a"
    Then the local of "a" is "<local>"

    Examples:
      | bind              | local        |
      | bind 127.0.0.3!6  | 127.0.0.3!6  |
      | bind 7            | ::!7         |

  @FOG_KERNEL_062
  Scenario: announce of a port alone listens on every address, whatever was bound before
    When the process opens "/net/tcp/clone" as "a"
    And the process writes "bind 127.0.0.2!0" to "a"
    And the process writes "announce 0" to "a"
    Then the local of "a" is "::!*"

  @FOG_KERNEL_063
  Scenario Outline: A ctl write the conversation cannot take fails with devip's and tcp.c's errors
    When the process opens "/net/tcp/clone" as "a"
    And the process writes "<message>" to "a"
    Then the call fails with "<error>"

    Examples:
      | message                    | error                   |
      | connect                    | bad args to connect     |
      | connect 127.0.0.1          | malformed address       |
      | connect nonesuch!80        | bad ip address syntax   |
      | connect 127.0.0.1!http     | bad numeric port        |
      | announce                   | bad args to announce    |
      | announce 127.0.0.1!x       | bad numeric port        |
      | frob                       | unknown control request |
      |                            | short control request   |
      | connect 127.0.0.1!80x      | bad numeric port        |
      | connect 127.0.0.1!         | bad numeric port        |
      | connect !80                | bad ip address syntax   |
      | bind                       | bad args to bind        |
      | bind !5                    | bad ip address syntax   |
      | bind 127.0.0.1!5x          | bad numeric port        |
      | announce *                 | announce of every port is not supported |
      | announce 127.0.0.1!*       | announce of every port is not supported |
      | announce 192.0.2.1!0       | not a local IP address  |

  @FOG_KERNEL_063
  Scenario: A call to a port nobody takes is refused, as the host refuses it, and leaves the conversation closed
    Given a port of the host's loopback that nobody takes
    When the process opens "/net/tcp/clone" as "a"
    And the process connects "a" to that port
    Then the call fails with "connection refused"
    And the status of "a" begins "Closed"

  @FOG_KERNEL_063
  Scenario: A port the host has given another cannot be announced
    Given a port of the host's loopback that the host holds for another
    When the process opens "/net/tcp/clone" as "a"
    And the process writes "announce 127.0.0.1!$taken" to "a"
    Then the call fails with "address in use"

  @FOG_KERNEL_063
  Scenario: A conversation's data, before it has a call, is hung up
    When the process opens "/net/tcp/clone" as "a"
    And the process opens "/net/tcp/0/data" as "d"
    And the process writes "hello" to "d"
    Then the call fails with "i/o on hungup channel"

  @FOG_KERNEL_063
  Scenario Outline: A conversation in use takes no second connect or announce
    Given a conversation announced on "127.0.0.1!0"
    When the process writes "<message>" to the announced conversation's ctl
    Then the call fails with "connection in use"

    Examples:
      | message              |
      | announce 127.0.0.1!0 |
      | connect 127.0.0.1!1  |

  @FOG_KERNEL_063
  Scenario: Only an announced conversation can be listened on
    When the process opens "/net/tcp/clone" as "a"
    And the process opens "/net/tcp/0/listen"
    Then the call fails with "not announced: '/net/tcp/0/listen'"

  @FOG_KERNEL_063
  Scenario Outline: Only the host owner may take every port with announce *
    Given the process has become "none"
    When the process opens "/net/tcp/clone" as "a"
    And the process writes "<message>" to "a"
    Then the call fails with "permission denied"

    Examples:
      | message              |
      | announce *           |
      | announce 127.0.0.1!* |

  @FOG_KERNEL_064
  Scenario Outline: hangup, or close, ends the conversation, and the other end reads the end of its data
    Given a conversation announced on "127.0.0.1!0"
    When a child connects to the announced port and writes "hello"
    And the process opens the announced conversation's listen
    And the process writes "<message>" to the call's ctl
    Then the child reads the end of its data

    Examples:
      | message |
      | hangup  |
      | close   |

  @FOG_KERNEL_064
  Scenario: hangup of an announced conversation gives its port back to the host
    Given a conversation announced on "127.0.0.1!0"
    When the process writes "hangup" to the announced conversation's ctl
    Then a call from the host to "127.0.0.1" at the announced port is refused

  @FOG_KERNEL_064
  Scenario: Closing a conversation's last ctl and data frees it and ends the call
    Given a conversation announced on "127.0.0.1!0"
    When a child connects to the announced port and writes "hello"
    And the process opens the announced conversation's listen
    And the process closes the call
    Then the child reads the end of its data
    And the status of the call's conversation begins "Closed"

  @FOG_KERNEL_065
  Scenario: A note interrupts a process waiting in listen
    Given a conversation announced on "127.0.0.1!0"
    When a child that continues after every note waits in the announced conversation's listen
    And the process posts "hangup" to the child
    Then the child's wait fails with "interrupted: '/net/tcp/0/listen'"

  @FOG_KERNEL_065
  Scenario: A process waiting in listen gives up when the conversation is hung up
    Given a conversation announced on "127.0.0.1!0"
    When a child that continues after every note waits in the announced conversation's listen
    And the process writes "hangup" to the announced conversation's ctl
    Then the child's wait fails with "listen hungup: '/net/tcp/0/listen'"

  @FOG_KERNEL_065
  Scenario: A conversation whose call the far end has not answered is Syn_sent, and a note interrupts the caller
    Given a listener of the host's loopback whose queue of calls is full
    When a child that continues after every note connects a conversation to the full listener
    Then the status of the child's conversation begins "Syn_sent"
    When the process posts "hangup" to the child
    Then the child's wait fails with "interrupted"

  @FOG_KERNEL_065
  Scenario: A note interrupts a process waiting to read data
    Given a conversation announced on "127.0.0.1!0"
    When a child connects to the announced port and writes "hello"
    And the process opens the announced conversation's listen
    And a child that continues after every note waits to read the call's data twice
    And the process posts "hangup" to the child
    Then the child's wait fails with "interrupted"

  @FOG_KERNEL_066
  Scenario: A conversation's files belong to the user who opened it
    When the process opens "/net/tcp/clone" as "a"
    Then "/net/tcp/clone" belongs to "network" with the mode 666
    And "/net/tcp/0/ctl" belongs to "glenda" with the mode 660
    And "/net/tcp/0/data" belongs to "glenda" with the mode 660
    And "/net/tcp/0/local" belongs to "glenda" with the mode 444
    And "/net/tcp/0/status" belongs to "glenda" with the mode 444

  @FOG_KERNEL_066
  Scenario Outline: Only a conversation's owner, or the host owner, may open its ctl and data
    When the process opens "/net/tcp/clone" as "a"
    And a child that becomes "none" opens "/net/tcp/0/<file>"
    Then the child's open fails with "permission denied: '/net/tcp/0/<file>'"

    Examples:
      | file |
      | ctl  |
      | data |

  @FOG_KERNEL_066
  Scenario: A user who is not the host owner opens the ctl of a conversation it owns
    When a child that becomes "none" clones a conversation and opens its ctl again
    Then the child's open succeeds

  @FOG_KERNEL_066
  Scenario: The host owner opens the ctl and data of a conversation another user owns
    When a child that becomes "none" clones a conversation and keeps it
    Then the process can open "/net/tcp/0/ctl" and "/net/tcp/0/data"

  @FOG_KERNEL_066
  Scenario: Opening the ctl of a free conversation makes the opener its owner
    When the process opens "/net/tcp/clone" as "a"
    And the process closes "a"
    Then "/net/tcp/0/ctl" belongs to "network" with the mode 666
    When a child that becomes "none" opens "/net/tcp/0/ctl" and keeps it
    Then the child's open succeeds
    And "/net/tcp/0/ctl" belongs to "none" with the mode 660

  @FOG_KERNEL_066
  Scenario: Only the conversation's owner, or the host owner, may listen on it
    Given a conversation announced on "127.0.0.1!0"
    When a child that becomes "none" opens "/net/tcp/0/listen"
    Then the child's open fails with "permission denied: '/net/tcp/0/listen'"
