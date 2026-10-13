@fog_wasi_runner
Feature: fog-wasi runs a Preview 1 module from the host as a Fog process
  fog-wasi takes the arguments Wasmtime takes for what wasi-testsuite asks of a runtime:
  "--env NAME=VALUE" for each environment variable, "--dir HOST::/" for the host directory the
  module is given, then the module and its arguments. It boots a Fog kernel of its own, copies the
  host directory into it, and runs the module as a child process whose environment holds only the
  variables given, whose descriptors 0, 1 and 2 are pipes to the runner's stdin, stdout and stderr,
  and whose namespace has the copy bound before "/", which it holds open as its preopen; the
  kernel's own names, /bin, /dev, /env, /fd and /tmp, show through beside the copy's. Once the
  module ends, the copy is written back over the host directory, so the host sees what the module
  did; what the module left as it was is not touched. The runner exits as the module did: with its exit code, 0 when _start returns, and 134,
  Wasmtime's, after a trap, which it names on stderr, and 1 when the module could not run, also
  named. Anything else it is given is refused with its usage and exit code 2. Symbolic links in the
  host directory have no Fog counterpart: they are left out of the copy and left as they are.

  Background:
    Given a host directory

  @WASI_DIFF_010
  Scenario Outline: The runner exits with the module's exit code
    When fog-wasi runs the module "<fixture>"
    Then fog-wasi exits with <code>

    Examples:
      | fixture | code |
      | exit-0  | 0    |
      | exit-3  | 3    |
      | hello   | 0    |

  @WASI_DIFF_010
  Scenario: A trap exits 134 and is named on stderr
    When fog-wasi runs the module "divide-by-zero"
    Then fog-wasi exits with 134
    And its stderr is "wasm-trap: integer divide by zero\n"

  @WASI_DIFF_010
  Scenario: A module Fog refuses exits 1, named on stderr
    When fog-wasi runs the module "start-section"
    Then fog-wasi exits with 1
    And its stderr is "sys: exec header invalid\n"

  @WASI_DIFF_010
  Scenario: The module's stdout is the runner's
    When fog-wasi runs the module "hello"
    Then its stdout is "hello"

  @WASI_DIFF_010
  Scenario: The module's arguments are the module's path and the arguments after it
    When fog-wasi runs the module "echo-args" with the arguments "daily report"
    Then its stdout is "<module>\ndaily\nreport\n"

  @WASI_DIFF_010
  Scenario: The module's environment holds only the variables given
    When fog-wasi runs the module "environ" with the options "--env region=north --env shift=night"
    Then its stdout is "region=north\nshift=night\n"

  @WASI_DIFF_010
  Scenario: The host directory is the preopen "/", stdin is the module's, and it holds nothing else
    Given the host directory holds "report" with "daily"
    When fog-wasi runs the trace interpreter on the host directory with the script:
      """
      fd_prestat_get 3
      fd_prestat_dir_name 3 1
      fd_fdstat_get 5
      path_open 3 0 "report" 0 2 0 0
      fd_read 4 5
      """
    Then its stdout is:
      """
      fd_prestat_get 3 -> ok tag=0 len=1
      fd_prestat_dir_name 3 1 -> ok "/"
      fd_fdstat_get 5 -> BADF
      path_open 3 0 "report" 0 2 0 0 -> ok 4
      fd_read 4 5 -> ok "daily"
      """

  @WASI_DIFF_010
  Scenario: What the module does to its directory is written back to the host
    Given the host directory holds "report" with "daily"
    And the host directory holds "old" with "x"
    When fog-wasi runs the trace interpreter on the host directory with the script:
      """
      path_open 3 0 "made" 1 0x40 0 0
      fd_write 4 "new"
      path_create_directory 3 "sub"
      path_unlink_file 3 "old"
      """
    Then the host directory holds only:
      | name   | contents    |
      | made   | new         |
      | report | daily       |
      | sub    | <directory> |

  @WASI_DIFF_010
  Scenario: What the module left as it was is not written back, so a read-only file stays as it is
    Given the host directory holds the read-only file "frozen" with "x"
    When fog-wasi runs the trace interpreter on the host directory with the script:
      """
      path_open 3 0 "frozen" 0 0x40 0 0
      path_create_directory 3 "sub"
      """
    Then its stdout is:
      """
      path_open 3 0 "frozen" 0 0x40 0 0 -> ACCES
      path_create_directory 3 "sub" -> ok
      """
    And the host file "frozen" was not written

  @WASI_DIFF_010
  Scenario: A file the module replaces with a directory, or a directory with a file, is replaced on the host
    Given the host directory holds "report" with "daily"
    And the host directory holds the directory "old"
    And the host directory holds "old/f" with "f"
    When fog-wasi runs the trace interpreter on the host directory with the script:
      """
      path_unlink_file 3 "report"
      path_create_directory 3 "report"
      path_unlink_file 3 "old/f"
      path_remove_directory 3 "old"
      path_open 3 0 "old" 1 0x40 0 0
      fd_write 4 "file"
      """
    Then its stdout is:
      """
      path_unlink_file 3 "report" -> ok
      path_create_directory 3 "report" -> ok
      path_unlink_file 3 "old/f" -> ok
      path_remove_directory 3 "old" -> ok
      path_open 3 0 "old" 1 0x40 0 0 -> ok 4
      fd_write 4 "file" -> ok 4
      """
    And the host directory holds only:
      | name   | contents    |
      | old    | file        |
      | report | <directory> |

  @WASI_DIFF_010
  Scenario: A symbolic link in the host directory is left out of the copy and left as it is, unless the module makes what has its name
    Given the host directory holds "report" with "daily"
    And the host directory holds the directory "sub"
    And the host directory holds "sub/f" with "f"
    And the host directory holds a symbolic link "kept" to "report"
    And the host directory holds a symbolic link "taken" to a file outside it holding "outside"
    And the host directory holds a symbolic link "shortcut" to "sub"
    When fog-wasi runs the trace interpreter on the host directory with the script:
      """
      path_filestat_get 3 0 "kept"
      path_open 3 0 "taken" 1 0x40 0 0
      fd_write 4 "made"
      path_open 3 0 "shortcut" 1 0x40 0 0
      fd_write 5 "made too"
      """
    Then its stdout is:
      """
      path_filestat_get 3 0 "kept" -> NOENT
      path_open 3 0 "taken" 1 0x40 0 0 -> ok 4
      fd_write 4 "made" -> ok 4
      path_open 3 0 "shortcut" 1 0x40 0 0 -> ok 5
      fd_write 5 "made too" -> ok 8
      """
    And the host directory holds only:
      | name     | contents             |
      | kept     | <link report>        |
      | report   | daily                |
      | shortcut | made too             |
      | sub      | <nonempty directory> |
      | taken    | made                 |
    And the file outside the host directory holds "outside"

  @WASI_DIFF_010
  Scenario Outline: An option fog-wasi does not take is refused with its usage
    When fog-wasi runs the module "hello" with the options "<options>"
    Then fog-wasi exits with 2
    And its stderr is "usage: fog-wasi [--env NAME=VALUE]... [--dir HOST::/] module.wasm [arg]...\n"

    Examples:
      | options                   | what                         |
      | --env region              | a variable without a value   |
      | --env =north              | a variable without a name    |
      | --dir /tmp::/data         | a directory not given as "/" |
      | --dir /tmp::/ --dir /tmp::/ | two directories            |
      | --wasm threads=y          | an option it does not take   |
      | --wasm threads::/         | an option it does not take, ending as a directory does |

  @WASI_DIFF_010
  Scenario Outline: Arguments without a module are refused with the usage
    When fog-wasi runs with the arguments "<arguments>"
    Then fog-wasi exits with 2
    And its stderr is "usage: fog-wasi [--env NAME=VALUE]... [--dir HOST::/] module.wasm [arg]...\n"

    Examples:
      | arguments           | what                              |
      |                     | nothing                           |
      | --env               | an option without its value       |
      | --env region=north  | an option and no module           |
      | --env a=b --dir     | an option after a complete one    |
