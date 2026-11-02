@fog_login
Feature: Stock 9front rcpu runs rc on Fog through tcp17019
  Fog runs aux/listen with tcp17019, as a 9front CPU server does. A call authenticates with p9any
  and dp9ik through the host owner's factotum, is encrypted with TLS-PSK keyed by the session
  secret, and runs the script rcpu sends, as the authenticated user, with rc; rcpu's files are
  served back over the same conversation and mounted at /mnt/term. 9front runs unchanged in QEMU
  from its release ISO; its ndb names Fog's auth server for the domain "fog". dp9ik.net's client,
  which fog is to be built on, logs in the same way, its tickets got from the same auth server.
  Scenarios are skipped unless FOG_9FRONT_ISO names a 9front amd64 ISO and qemu-system-x86_64 is on the path.

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

  @FOG_LOGIN_009
  Scenario: 9front's secstore client gets a file from Fog's secstored
    Given glenda's secstore on Fog holds the file "notes" with "kept"
    When 9front runs "auth/secstore -s tcp!fog!5356 -u glenda -G notes" and gives glenda's secstore password
    Then it prints "kept"

  @FOG_LOGIN_009
  Scenario: 9front's secstore client puts a file in Fog's secstored, which Fog's client gets
    Given glenda has a secstore account on Fog
    When 9front runs "cd /tmp; echo put by 9front >fromnine; auth/secstore -s tcp!fog!5356 -u glenda -p fromnine" and gives glenda's secstore password
    And glenda's secstore on Fog holds "fromnine"
    And Fog runs "auth/secstore -i -s tcp!fog!5356 -u glenda -G fromnine" with glenda's secstore password
    Then it prints "put by 9front"

  @FOG_LOGIN_009
  Scenario: Fog's secstore client gets a file from 9front's secstored
    Given glenda's secstore on 9front holds the file "notes" with "kept on 9front"
    When Fog runs "auth/secstore -i -s tcp!9front!5356 -u glenda -G notes" with glenda's secstore password
    Then it prints "kept on 9front"

  @FOG_LOGIN_012
  Scenario: dp9ik.net's client logs in to Fog, getting its tickets from Fog's auth server
    When dp9ik.net's client calls 17019 as "glenda" with the password "secretpw", getting its tickets from 567
    Then its ticket names "glenda" as the client's and the server's user
    And over TLS-PSK keyed by its secret, the script "echo hello; cat /dev/user" prints "hello\nglenda"

  @FOG_LOGIN_012
  Scenario: dp9ik.net's client is asked again for a password whose ticket does not open
    When dp9ik.net's client calls 17019 as "glenda" with the passwords "wrongpw" and then "secretpw", getting its tickets from 567
    Then the password was asked for as "glenda@fog dp9ik password", and again after a mismatch
    And over TLS-PSK keyed by its secret, the script "echo hello" prints "hello"

  # fog runs in the test's process with the local machine it is given, its $cpu and $auth the
  # host's forwarded 17019 and 567 and its $PASS glenda's, unless a scenario says otherwise.
  @FOG_LOGIN_005
  Scenario: fog sends the script stock rcpu sends
    Given Fog's tcp17019 keeps the script each call sends and runs none
    When 9front's rcpu calls from the directory "/mnt/term/root/usr/glenda" with the command "ls /bin"
    And fog calls from the local directory "/usr/glenda" with the command "ls /bin"
    Then the two scripts are the same

  @FOG_LOGIN_005
  Scenario: fog runs a command with the local standard input and output
    When fog runs "-u glenda -c cat" with the standard input "a\nb\n"
    Then fog prints "a\nb"
    And fog exits 0

  @FOG_LOGIN_005
  Scenario: fog passes each argument after the command to rc
    When fog runs "-u glenda -c echo 'two words' three"
    Then fog prints "two words three"
    And fog exits 0

  @FOG_LOGIN_005
  Scenario: fog exits 1 when rc's status is not empty
    When fog runs "-u glenda -c 'exit oops'"
    Then fog exits 1

  @FOG_LOGIN_005
  Scenario: fog starts rc in the local directory, under /mnt/term/root
    Given fog's local directory holds "app.txt" with "from the client"
    When fog runs "-u glenda -c 'pwd; cat app.txt'" in that directory
    Then fog prints that directory under "/mnt/term/root", and then "from the client"

  @FOG_LOGIN_005
  Scenario: rc writes the local files through /mnt/term/root
    Given fog's local directory holds "app.txt" with "from the client"
    When fog runs "-u glenda -c 'echo from Fog >made.txt'" in that directory
    Then fog exits 0
    And fog's local directory holds "made.txt" with "from Fog\n"

  @FOG_LOGIN_005
  Scenario: fog on Windows names drives as drawterm does
    Given a Windows machine whose drives are "C:" and "D:"
    When fog runs "-u glenda -c 'cd /mnt/term/root; echo *'" from "C:\work"
    Then fog prints "C: D:"

  # As after Del in stock rcpu, the note copier holds the client's files open, so the session ends
  # when fog's call does.
  @FOG_LOGIN_005
  Scenario: Ctrl-C in fog interrupts the command on Fog
    Given fog runs "-u glenda" interactively
    When the user types "sleep 100" and then Ctrl-C
    Then rc on Fog is interrupted, and typing "echo after" prints "after"

  @FOG_LOGIN_005
  Scenario: fog reports a lost connection
    Given fog runs "-u glenda -c 'sleep 100'" until rc is running
    When the host drops fog's call
    Then fog prints "lost connection" on its standard error
    And fog exits 1

  @FOG_LOGIN_005
  Scenario: Without $PASS fog asks for the password on the terminal, and again after a mismatch
    Given fog has no $PASS, and the terminal gives "wrongpw" and then "secretpw"
    When fog runs "-u glenda -c 'echo in'"
    Then the terminal was asked "glenda@fog dp9ik password: " twice
    And fog prints "?password mismatch with auth server" on its standard error
    And fog prints "in"

  @FOG_LOGIN_005
  Scenario: A wrong $PASS ends fog
    Given fog's $PASS is "wrongpw"
    When fog runs "-u glenda -c 'echo in'"
    Then fog prints "fog: wrong password" on its standard error
    And fog exits 1

  @FOG_LOGIN_005
  Scenario Outline: fog takes rcpu's usage without -k, -P, -p and -t
    When fog runs "<args>"
    Then fog prints "usage: fog [-u user] [-h host] [-c cmd arg ...]" on its standard error
    And fog exits 1

    Examples:
      | args            |
      | -p              |
      | -k proto=dp9ik  |
      | -u              |
      | -u glenda extra |

  @FOG_LOGIN_005
  Scenario: rc reads the terminal's lines, a byte at a time, and writes to it through /dev/cons
    Given fog's terminal gives the lines "first line\nsecond"
    When fog runs "-u glenda -c 'echo to the terminal >/dev/cons; read </dev/cons; read </dev/cons; read </dev/cons || echo at the end'"
    Then fog prints "to the terminal\nfirst line\nsecond\nat the end"

  @FOG_LOGIN_005
  Scenario: fog serves the terminal for reading and writing, standard input for reading, and its output and errors for writing
    When fog runs "-u glenda -c 'ls -l /mnt/term/dev/cons /mnt/term/fd/1? | sed ''s/ .* / /'''"
    Then fog prints "--rw-rw-rw- /mnt/term/dev/cons\n--r--r--r-- /mnt/term/fd/10\n---w--w--w- /mnt/term/fd/11\n---w--w--w- /mnt/term/fd/12"

  @FOG_LOGIN_005
  Scenario: fog logs in as the local login name when it is given no user
    When fog runs "-c 'cat /dev/user'"
    Then fog prints "glenda"
    And fog exits 0

  @FOG_LOGIN_005
  Scenario: fog cannot log in as a user the auth server does not know
    When fog runs "-u nobody -c 'echo in'"
    Then fog exits 1

  @FOG_LOGIN_005
  Scenario: Without $PASS fog asks for the password once, and says nothing of a mismatch, when it is right
    Given fog has no $PASS, and the terminal gives "secretpw"
    When fog runs "-u glenda -c 'echo in'"
    Then fog prints "in"
    And fog does not print "?password mismatch" on its standard error

  @FOG_LOGIN_005
  Scenario: fog run as a program takes its standard input, environment and exit code from the shell
    When the shell runs fog "-u glenda -c cat" with the standard input "a\nb\n"
    Then the program prints "a\nb"
    And the program exits 0
