@fog_login
Feature: Stock 9front rcpu runs rc on Fog through tcp17019
  Fog runs aux/listen with tcp17019, as a 9front CPU server does. A call authenticates with p9any
  and dp9ik through the host owner's factotum, is encrypted with TLS-PSK keyed by the session
  secret, and runs the script rcpu sends, as the authenticated user, with rc; rcpu's files are
  served back over the same conversation and mounted at /mnt/term. 9front runs unchanged in QEMU
  from its release ISO; its ndb names Fog's auth server for the domain "fog". Scenarios are skipped
  unless FOG_9FRONT_ISO names a 9front amd64 ISO and qemu-system-x86_64 is on the path.

  Background:
    Given a Fog host listening on tcp port 17019, and the auth server for the domain "fog" on 567
    And the user "glenda" with the password "secretpw"

  @FOG_LOGIN_001
  Scenario: Stock rcpu runs a command on Fog as the user
    When 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'echo hello; cat /dev/user'"
    Then it prints "hello\nglenda"
    And its status is empty

  @FOG_LOGIN_001
  Scenario: Stock rcpu gets rc's status back
    When 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'exit oops'"
    Then its status is "oops"

  @FOG_LOGIN_001
  Scenario: The client's files are at /mnt/term
    Given 9front's "/tmp/note" holds "from the terminal"
    When 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'cat /mnt/term/tmp/note'"
    Then it prints "from the terminal"

  @FOG_LOGIN_001
  Scenario: The session starts in the client's directory when Fog has it
    When 9front runs "cd /tmp; rcpu -h tcp!fog!17019 -u glenda -c pwd"
    Then it prints "/tmp"

  @FOG_LOGIN_001
  Scenario: A client's directory Fog does not have is reported, as rcpu's script reports it
    When 9front runs "cd /sys/src; rcpu -h tcp!fog!17019 -u glenda -c 'echo here'"
    Then it prints "can't change directory: /sys/src" on its standard error
    And it prints "here"

  @FOG_LOGIN_002
  Scenario: A wrong password is refused before anything runs
    When 9front runs rcpu as "glenda" with the password "wrongpw"
    Then no process is started for the call
    And factotum asks for the key again

  @FOG_LOGIN_002
  Scenario: A call that does not complete p9any is closed without TLS
    When a client connects to 17019 and sends "nonsense\0"
    Then the host closes the call
    And no process is started for the call

  # After Del, rcpu's note copier, which posted the interrupt and so was not sent it, holds the
  # client's files open, so the session ends when the client's call does, as 9front's does.

  @FOG_LOGIN_003
  Scenario: Del interrupts the command and the session goes on
    When 9front runs "rcpu -h tcp!fog!17019 -u glenda" interactively
    And it types "sleep 100" and then Del
    Then rc on Fog is interrupted and prompts again
    And typing "echo still here" prints "still here"

  @FOG_LOGIN_003
  Scenario: Closing the client hangs up the session's processes
    When 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'sleep 100'" in the background
    And 9front hangs up the client's call
    Then every process of the session's note group on Fog has exited
