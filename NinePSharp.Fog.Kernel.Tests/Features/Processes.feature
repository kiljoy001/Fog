@fog_kernel_processes
Feature: Programs run as Plan 9 processes on Fog's namespace
  The kernel runs programs as Plan 9 processes over Fog's virtual process layer: each has a
  namespace, a descriptor table and an environment group, which fork shares or copies as rfork(2)
  says. exec(2) reads the file's header: #! names an interpreter, and a managed program's file names
  a .NET program. A process ends with exits(2), and its parent's wait(2) reads a message that is
  empty on success and otherwise "name pid: status", where name is the last element of the file
  executed; a program that returns from main exits with "main", as libc's _callmain does. The
  environment is files in /env, shared by the processes of an environment group. A failed walk
  names the path as far as the element that failed, as 9front's namec does.

  Background:
    Given a kernel whose /bin holds the managed programs "true", "false", "args", "return" and "crash"

  @FOG_KERNEL_001
  Scenario: A program that exits cleanly leaves an empty wait message
    When a process runs "/bin/true"
    Then its parent's wait message for it is empty
    And no file is left open

  @FOG_KERNEL_001
  Scenario: A program that exits with a status leaves its name, pid and status
    When a process runs "/bin/false"
    Then its parent's wait message for it is "false <pid>: false"

  @FOG_KERNEL_001
  Scenario: A program that returns from main without exits leaves the status main
    When a process runs "/bin/return"
    Then its parent's wait message for it is "return <pid>: main"

  @FOG_KERNEL_001
  Scenario: A program that throws exits as a trapped process does
    When a process runs "/bin/crash"
    Then its parent's wait message for it is "crash <pid>: sys: boom"

  @FOG_KERNEL_001
  Scenario: A child that exits before exec carries its parent's name
    When a process forks a child that exits with "oops"
    Then its parent's wait message for it is "*init* <pid>: oops"

  @FOG_KERNEL_001
  Scenario: A child that returns without exits leaves the status main
    When a process forks a child that returns
    Then its parent's wait message for it is "*init* <pid>: main"

  @FOG_KERNEL_002
  Scenario: A #! script runs its interpreter with the script's name as argv[0] and its full name after the interpreter's arguments
    Given the file "/tmp/script" holds "#!/bin/args -x\n"
    When a process runs "/tmp/script" with arguments "a b"
    Then the program saw the arguments "script -x /tmp/script a b"

  @FOG_KERNEL_002
  Scenario: A #! script may name another script as its interpreter
    Given the file "/tmp/inner" holds "#!/bin/args inner\n"
    And the file "/tmp/outer" holds "#!/tmp/inner outer\n"
    When a process runs "/tmp/outer" with arguments "a"
    Then the program saw the arguments "outer inner /tmp/inner outer /tmp/outer a"

  @FOG_KERNEL_002
  Scenario: A #! line may name just the interpreter
    Given the file "/tmp/script" holds "#!/bin/args\n"
    When a process runs "/tmp/script" with arguments "a"
    Then the program saw the arguments "script /tmp/script a"

  @FOG_KERNEL_002
  Scenario: A #! line ends at a NUL byte
    Given the file "/tmp/script" holds "#!/bin/args x\0y z\n"
    When a process runs "/tmp/script"
    Then the program saw the arguments "script x /tmp/script"

  @FOG_KERNEL_002
  Scenario: A #! line may hold the interpreter and 31 arguments
    Given the file "/tmp/script" runs "/bin/args" with 31 interpreter arguments
    When a process runs "/tmp/script"
    Then the program saw 33 arguments

  @FOG_KERNEL_002
  Scenario: A #! line with the interpreter and 32 arguments is an invalid exec header
    Given the file "/tmp/script" runs "/bin/args" with 32 interpreter arguments
    When a process runs "/tmp/script"
    Then exec fails with "exec header invalid"

  @FOG_KERNEL_002
  Scenario: A #! line naming a missing interpreter fails as open does
    Given the file "/tmp/script" holds "#!/bin/nonesuch\n"
    When a process runs "/tmp/script"
    Then exec fails with "file does not exist: '/bin/nonesuch'"

  @FOG_KERNEL_002
  Scenario: More than eight levels of #! are an invalid exec header
    Given nine scripts "/tmp/s1" to "/tmp/s9", each naming the next as its interpreter, and "/tmp/s9" naming "/bin/true"
    When a process runs "/tmp/s1"
    Then exec fails with "exec header invalid"

  @FOG_KERNEL_002
  Scenario Outline: A file that is neither a program nor a script is an invalid exec header
    Given the file "/tmp/bad" holds "<contents>"
    When a process runs "/tmp/bad"
    Then exec fails with "exec header invalid"

    Examples:
      | contents          |
      | plain text\\n     |
      | #!                |
      | #!/bin/true       |
      | #!\\n             |
      | \0fog nonesuch\\n |
      | \0fog true        |
      | x!/bin/true\\n    |
      | #!\0/bin/true\\n  |

  @FOG_KERNEL_002
  Scenario: Exec of a missing file fails as open does
    When a process runs "/bin/nonesuch"
    Then exec fails with "file does not exist: '/bin/nonesuch'"

  @FOG_KERNEL_002
  Scenario Outline: Exec of a directory fails
    When a process runs "<path>"
    Then exec fails with "<error>"

    Examples:
      | path | error                               |
      | /bin | cannot exec directory: '/bin'       |
      | /    | cannot exec directory               |

  @FOG_KERNEL_003
  Scenario: A forked child has a copy of its parent's descriptors
    Given the file "/tmp/f" holds "data"
    And a process with "/tmp/f" open as descriptor 3
    When it forks a child with RFFDG that closes descriptor 3
    Then descriptor 3 is still open in the parent

  @FOG_KERNEL_003
  Scenario: A forked child shares its parent's descriptors without RFFDG
    Given the file "/tmp/f" holds "data"
    And a process with "/tmp/f" open as descriptor 3
    When it forks a child without RFFDG that closes descriptor 3
    Then descriptor 3 is closed in the parent

  @FOG_KERNEL_003
  Scenario: A child's descriptors are released when it exits
    Given the file "/tmp/f" holds "data"
    And a process with "/tmp/f" open as descriptor 3
    When it forks a child with RFFDG that runs "/bin/true"
    And it closes descriptor 3
    Then no file is left open

  @FOG_KERNEL_003
  Scenario: exec closes the descriptors opened with OCEXEC
    Given the file "/tmp/f" holds "data"
    And a process with "/tmp/f" open as descriptor 3 with OCEXEC
    When it forks a child without RFFDG that runs "/bin/true"
    Then descriptor 3 is closed in the parent

  @FOG_KERNEL_003
  Scenario: rfork may not both copy and clear the environment
    When a process forks a child with RFENVG and RFCENVG
    Then it fails with "bad arg in system call"

  @FOG_KERNEL_004
  Scenario: wait with no children fails
    When a process with no children waits
    Then wait fails with "no living children"

  @FOG_KERNEL_004
  Scenario: wait fails once every child has been waited for
    When a process runs "/bin/true"
    And it waits again
    Then wait fails with "no living children"

  @FOG_KERNEL_005
  Scenario: Processes in one environment group share /env
    Given a process that writes "hello" to "/env/greeting"
    When it forks a child without RFENVG that reads "/env/greeting"
    Then the child read "hello"

  @FOG_KERNEL_005
  Scenario: A child with RFENVG has a copy of the environment
    Given a process that writes "hello" to "/env/greeting"
    When it forks a child with RFENVG that writes "bye" to "/env/greeting"
    Then the parent reads "hello" from "/env/greeting"

  @FOG_KERNEL_005
  Scenario: A child with RFCENVG starts with an empty environment
    Given a process that writes "hello" to "/env/greeting"
    When it forks a child with RFCENVG that lists "/env"
    Then the child listed nothing

  @FOG_KERNEL_005
  Scenario: A child with RFENVG lists a copy of the environment
    Given a process that writes "hello" to "/env/greeting"
    When it forks a child with RFENVG that lists "/env"
    Then the child listed "greeting"

  @FOG_KERNEL_005
  Scenario: A child with RFENVG adds to its own copy of the environment
    Given a process that writes "hello" to "/env/greeting"
    When it forks a child with RFENVG that writes "bye" to "/env/farewell"
    Then the parent lists "greeting" in "/env"

  @FOG_KERNEL_006
  Scenario Outline: A failed walk names the path as far as the failing element
    Given the file "/tmp/f" holds "data"
    When a process <call> "<path>"
    Then it fails with "<error>"

    Examples:
      | call    | path                 | error                                   |
      | opens   | /tmp/missing/file    | file does not exist: '/tmp/missing'     |
      | opens   | /tmp/./missing/file  | file does not exist: '/tmp/./missing'   |
      | opens   | //tmp//missing       | file does not exist: '//tmp//missing'   |
      | opens   | /tmp/f/x             | not a directory: '/tmp/f'               |
      | creates | /tmp/missing/file    | file does not exist: '/tmp/missing'     |
      | removes | /tmp/missing         | file does not exist: '/tmp/missing'     |
      | enters  | /tmp/f               | not a directory: '/tmp/f'               |
      | enters  | /nonesuch            | file does not exist: '/nonesuch'        |

  @FOG_KERNEL_006
  Scenario: Relative paths start at the current directory
    Given the file "/tmp/f" holds "data"
    When a process enters "/tmp"
    Then reading "f" gives "data"
    And opening "f/x" fails with "not a directory: 'f'"
    And opening "/tmp/missing/x" fails with "file does not exist: '/tmp/missing'"

  @FOG_KERNEL_006
  Scenario Outline: A descriptor that is not open is an error
    When a process <call> descriptor 7
    Then it fails with "fd out of range or not open"

    Examples:
      | call        |
      | reads       |
      | writes      |
      | closes      |
      | duplicates  |

  @FOG_KERNEL_007
  Scenario: A write inside a file replaces the bytes it covers
    Given the file "/tmp/f" holds "hello"
    When a process writes "J" at the start of "/tmp/f"
    Then reading "/tmp/f" gives "Jello"

  @FOG_KERNEL_007
  Scenario: Opening with OTRUNC empties a file
    Given the file "/tmp/f" holds "hello"
    When a process opens "/tmp/f" with OTRUNC
    Then reading "/tmp/f" gives ""

  @FOG_KERNEL_007
  Scenario: Creating an existing file empties it
    Given the file "/tmp/f" holds "hello"
    When a process creates "/tmp/f"
    Then reading "/tmp/f" gives ""

  @FOG_KERNEL_007
  Scenario: remove deletes a file
    Given the file "/tmp/f" holds "hello"
    When a process removes "/tmp/f"
    Then opening "/tmp/f" fails with "file does not exist: '/tmp/f'"

  @FOG_KERNEL_007
  Scenario: remove of a directory that holds files fails
    Given the directory "/tmp/d" holds the file "x"
    When a process removes "/tmp/d"
    Then it fails with "has children"

  @FOG_KERNEL_007
  Scenario: A removed file stays readable through a descriptor still open on it
    Given the file "/tmp/f" holds "hello"
    And a process with "/tmp/f" open as descriptor 3
    When a process removes "/tmp/f"
    Then reading descriptor 3 gives "hello"

  @FOG_KERNEL_007
  Scenario: Reads continue from where the last one ended
    Given the file "/tmp/f" holds "hello"
    And a process with "/tmp/f" open as descriptor 3
    Then reading descriptor 3 two bytes at a time gives "he", "ll", "o" and then nothing

  @FOG_KERNEL_007
  Scenario: A read beyond the end of a truncated file returns nothing
    Given the file "/tmp/f" holds "hello"
    And a process with "/tmp/f" open as descriptor 3
    And descriptor 3 has been read to the end
    When a process opens "/tmp/f" with OTRUNC
    Then reading descriptor 3 gives ""
