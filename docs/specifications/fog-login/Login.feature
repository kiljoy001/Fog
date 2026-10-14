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

  @FOG_LOGIN_003
  Scenario: Closing the client hangs up the session's processes
    Given 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'sleep 100'"
    When the rcpu client is killed
    Then every process of the session's note group on Fog has exited

  @FOG_LOGIN_004
  Scenario: drawterm without its keyboard gets rc
    When drawterm runs "-B -h fog -a fog -u glenda -c 'echo hello'"
    Then it prints "hello"

  @FOG_LOGIN_005
  Scenario: fog sends the script stock rcpu sends
    When fog and 9front's rcpu each connect with the directory "/usr/glenda" and the command "ls /bin"
    Then the bytes each sends after TLS and before 9P are the same

  @FOG_LOGIN_005
  Scenario: fog runs a command with the local standard input and output
    When the shell runs "printf 'a\nb\n' | fog -h fog -u glenda -c 'wc -l'"
    Then it prints "2"
    And fog exits 0

  @FOG_LOGIN_005
  Scenario: fog exits 1 when rc's status is not empty
    When the shell runs "fog -h fog -u glenda -c 'exit oops'"
    Then fog exits 1

  @FOG_LOGIN_005
  Scenario: fog starts rc in the local directory, under /mnt/term/root
    Given the local directory "/home/glenda/work" holds "app.wasm"
    When the shell runs "cd /home/glenda/work; fog -h fog -u glenda -c 'pwd; ls'"
    Then it prints "/mnt/term/root/home/glenda/work\napp.wasm"

  @FOG_LOGIN_005
  Scenario: fog on Windows names drives as drawterm does
    Given a Windows machine whose drives are "C:" and "D:"
    When PowerShell runs "fog -h fog -u glenda -c 'ls /mnt/term/root'"
    Then it prints "/mnt/term/root/C:\n/mnt/term/root/D:"

  @FOG_LOGIN_005
  Scenario: Ctrl-C in fog interrupts the command on Fog
    Given fog runs "-h fog -u glenda" interactively
    When the user types "sleep 100" and then Ctrl-C
    Then rc on Fog is interrupted and prompts again

  @FOG_LOGIN_005
  Scenario: fog reports a lost connection
    Given fog runs "-h fog -u glenda -c 'sleep 100'"
    When the host drops the connection
    Then fog prints "lost connection" on its standard error
    And fog exits 1

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
