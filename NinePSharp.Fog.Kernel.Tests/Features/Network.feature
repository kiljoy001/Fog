@fog_kernel_network
Feature: /net/tcp is ip(3)'s file interface over the host's sockets, as 9front binds #I on /net
  The device #I serves tcp as devip and tcp.c do, over the host's sockets as drawterm's devip does. Opening
  /net/tcp/clone reserves a conversation and opens its ctl, which reads the conversation's number.
  Writing "connect addr!port" to a ctl calls out, "announce [addr!]port" takes calls, "bind" sets
  the local port and "hangup" ends the conversation; opening an announced conversation's listen
  waits for a call and opens the new conversation's ctl. data carries the conversation's bytes, and
  local and remote read each end as addr!port. A conversation's ctl, data and listen belong to the
  user who opened it, with the mode 660, whose owner bits let anyone open ctl and data as devip's
  ipopen does, though only the owner or the host owner may listen; local, remote and status may be
  read by anyone;
  when the last of its ctl and data closes, its socket closes and it is free for the next clone. A
  note interrupts a process waiting in listen, connect or a read of data. Here the conversations
  run over the host's loopback.

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
  Scenario: A conversation announced on a port alone listens on every address
    Given a conversation announced on "0"
    Then its local is "::" and a port above 0

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

  @FOG_KERNEL_063
  Scenario: A call to a port nobody takes is refused, as the host refuses it
    Given a port of the host's loopback that nobody takes
    When the process opens "/net/tcp/clone" as "a"
    And the process connects "a" to that port
    Then the call fails with "connection refused"

  @FOG_KERNEL_063
  Scenario: A conversation in use takes no second connect or announce
    Given a conversation announced on "127.0.0.1!0"
    When the process writes "announce 127.0.0.1!0" to the announced conversation's ctl
    Then the call fails with "connection in use"

  @FOG_KERNEL_063
  Scenario: Only an announced conversation can be listened on
    When the process opens "/net/tcp/clone" as "a"
    And the process opens "/net/tcp/0/listen"
    Then the call fails with "not announced"

  @FOG_KERNEL_063
  Scenario: Only the host owner may take every port with announce *
    Given the process has become "none"
    When the process opens "/net/tcp/clone" as "a"
    And the process writes "announce *" to "a"
    Then the call fails with "permission denied"

  @FOG_KERNEL_064
  Scenario: hangup ends the conversation, and the other end reads the end of its data
    Given a conversation announced on "127.0.0.1!0"
    When a child connects to the announced port and writes "hello"
    And the process opens the announced conversation's listen
    And the process writes "hangup" to the call's ctl
    Then the child reads the end of its data

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
  Scenario: Only the conversation's owner, or the host owner, may listen on it
    Given a conversation announced on "127.0.0.1!0"
    When a child that becomes "none" opens "/net/tcp/0/listen"
    Then the child's open fails with "permission denied"
