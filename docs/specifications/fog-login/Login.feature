@fog_login @design
Feature: A user logs in to Fog and gets rc, from 9front, drawterm or any shell
  Fog listens on tcp port 17019 with aux/listen, as a 9front CPU server does. A call authenticates with
  p9any and dp9ik, is encrypted with TLS-PSK keyed by the session secret, and then runs the
  script the client sends, as the authenticated user, with rc. The client's files are served back
  over the same conversation and mounted at /mnt/term. Stock rcpu and drawterm are clients, and so
  is fog, a .NET rcpu client for PowerShell and Unix shells. These acceptance scenarios are
  pending production bindings.

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

  @FOG_LOGIN_003
  Scenario: Del interrupts the command and the session goes on
    Given 9front runs "rcpu -h tcp!fog!17019 -u glenda" interactively
    When it types "sleep 100" and then Del
    Then rc on Fog is interrupted and prompts again

  # After Del, rcpu's note copier, which posted the interrupt and so was not sent it, holds the
  # client's files open, so the session ends when the client's call does, as 9front's does.

  @FOG_LOGIN_003
  Scenario: Closing the client hangs up the session's processes
    Given 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'sleep 100'"
    When the client's call is hung up, as killing the client hangs it up
    Then every process of the session's note group on Fog has exited

  @FOG_LOGIN_004
  Scenario: drawterm without its keyboard gets rc
    When drawterm runs "-B -h fog -a fog -u glenda -c 'echo hello'"
    Then it prints "hello"

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

  @FOG_LOGIN_006
  Scenario: An application installed from a session runs as a process and serves its files
    Given the local directory holds the WASM application "app.wasm"
    When the shell runs "fog -h fog -u glenda -c 'cp app.wasm /bin/app; app'"
    Then "/bin/app" on Fog holds the application's bytes
    And it runs as a process of the session

  @FOG_LOGIN_007
  Scenario: A first session finds its home and the installed applications
    Given "/bin" on Fog holds the application "app"
    When the shell runs "fog -h fog -u glenda -c 'echo $home; ls /bin/app'"
    Then it prints "/usr/glenda\n/bin/app"

  @FOG_LOGIN_007
  Scenario: An application installed in a session is still there after the host restarts
    Given the shell has run "fog -h fog -u glenda -c 'cp app.wasm /bin/app'"
    When the host is stopped and started again on the same gefs store
    And the shell runs "fog -h fog -u glenda -c 'ls /bin/app'"
    Then it prints "/bin/app"

  @FOG_LOGIN_008
  Scenario: The listener authenticates through the host owner's factotum
    Given the host owner's factotum is at "/srv/factotum"
    When 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'cat /dev/user'"
    Then it prints "glenda"
    And the host owner's factotum logged the dp9ik authentication of "glenda"

  @FOG_LOGIN_008
  Scenario: A capability is used once
    Given the host owner's factotum has made a capability for "glenda"
    When a process writes it to "/dev/capuse" twice
    Then the first write makes the process "glenda"
    And the second write fails

  @FOG_LOGIN_008
  Scenario: Only the host owner makes capabilities
    When a process of "glenda" writes to "/dev/caphash"
    Then the write fails

  @FOG_LOGIN_009
  Scenario: A session's factotum takes the user's keys from secstore
    Given glenda's secstore holds the file "factotum" with "key proto=pass server=example user=glenda !password=x"
    When 9front runs "rcpu -h tcp!fog!17019 -u glenda" interactively
    And it gives glenda's secstore password
    And it types "cat /mnt/factotum/ctl"
    Then it prints "key proto=pass server=example user=glenda !password?"

  @FOG_LOGIN_009
  Scenario: 9front's secstore client fetches a file from Fog's secstored
    Given glenda's secstore on Fog holds the file "notes" with "kept"
    When 9front runs "auth/secstore -s tcp!fog!5356 -u glenda -G notes"
    And it gives glenda's secstore password
    Then it prints "kept"

  @FOG_LOGIN_009
  Scenario: drawterm's secstore file is loaded into the session's factotum
    Given glenda's secstore holds the file "factotum" with "key proto=pass server=example user=glenda !password=x"
    When drawterm runs "-h fog -a fog -s fog -u glenda" and is given both passwords
    Then the session's "/mnt/factotum/ctl" lists "key proto=pass server=example user=glenda !password?"

  @FOG_LOGIN_010
  Scenario: A process on Fog dials out through /net
    Given the 9front oracle serves tcp port 7 with "/rc/bin/service/!tcp7"
    When a process on Fog dials "tcp!oracle!7", writes "hello" and reads
    Then it reads "hello"

  @FOG_LOGIN_010
  Scenario: Opening clone reserves a conversation and gives its number
    When glenda's session runs "cat /net/tcp/clone"
    Then it prints a number n
    And "/net/tcp/n" holds "ctl", "data", "listen", "local", "remote" and "status"

  @FOG_LOGIN_010
  Scenario: aux/listen on Fog takes a call from 9front
    Given Fog runs "aux/listen -q -d /rc/bin/service tcp" with the service "tcp17019"
    When 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'cat /dev/user'"
    Then it prints "glenda"
    And the call's process on Fog started as "none"

  @FOG_LOGIN_010
  Scenario: An announce the host refuses fails with the host's error
    Given a program on the host already takes calls on tcp port 8080
    When a process on Fog writes "announce *!8080" to a tcp conversation's ctl
    Then the write fails with the host's error

  @FOG_LOGIN_010
  Scenario: A note interrupts a process waiting for a call
    Given a process on Fog has opened a tcp conversation's listen and is waiting for a call
    When the process is sent the note "interrupt"
    Then the open fails "interrupted"

  @FOG_LOGIN_010
  Scenario: cs translates an address for dial
    When glenda's session runs "ndb/csquery /net/cs tcp!127.0.0.1!17019"
    Then it prints "/net/tcp/clone 127.0.0.1!17019"

  @FOG_LOGIN_011
  Scenario: An account that needs STA logs in with its authenticator's code
    Given glenda's secstore account needs STA
    And Keycloak's realm has the user "glenda" with a TOTP authenticator
    When 9front runs "auth/secstore -s tcp!fog!5356 -u glenda -G notes"
    And it gives glenda's secstore password
    And it gives the authenticator's current code at "STA PIN+SecureID"
    Then it prints what glenda's secstore holds in "notes"

  @FOG_LOGIN_011
  Scenario: A code Keycloak refuses ends the login
    Given glenda's secstore account needs STA
    When glenda logs in to secstore with glenda's password and the code "000000"
    Then the login fails "authentication failed"
    And secstored logs "secureidcheck denied glenda"

  @FOG_LOGIN_011
  Scenario: A user disabled in Keycloak cannot log in with a good code
    Given glenda's secstore account needs STA
    And Keycloak's user "glenda" is disabled
    When glenda logs in to secstore with glenda's password and the authenticator's current code
    Then the login fails "authentication failed"

  @FOG_LOGIN_012
  Scenario: dp9ik.net's client logs in, getting its tickets from Fog's auth server
    When dp9ik.net's client calls 17019 as "glenda" with the password "secretpw", getting its tickets from 567
    Then its ticket names "glenda" as the client's and the server's user
    And over TLS-PSK keyed by its secret, the script "echo hello; cat /dev/user" prints "hello\nglenda"

  @FOG_LOGIN_012
  Scenario: dp9ik.net's client is asked again for a password whose ticket does not open
    When dp9ik.net's client calls 17019 as "glenda" with the passwords "wrongpw" and then "secretpw", getting its tickets from 567
    Then the password was asked for as "glenda@fog dp9ik password", and again after a mismatch
    And over TLS-PSK keyed by its secret, the script "echo hello" prints "hello"
