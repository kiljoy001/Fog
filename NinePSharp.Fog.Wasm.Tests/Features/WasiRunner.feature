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
  Wasmtime's, after a trap, which it names on stderr. Anything else it is given is refused with its
  usage and exit code 2.

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
  Scenario: The host directory is the preopen "/", and stdin is the module's
    Given the host directory holds "report" with "daily"
    When fog-wasi runs the trace interpreter on the host directory with the script:
      """
      fd_prestat_get 3
      fd_prestat_dir_name 3 1
      path_open 3 0 "report" 0 2 0 0
      fd_read 4 5
      """
    Then its stdout is:
      """
      fd_prestat_get 3 -> ok tag=0 len=1
      fd_prestat_dir_name 3 1 -> ok "/"
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
      path_create_directory 3 "sub"
      """
    Then fog-wasi exits with 0
    And the host file "frozen" was not written

  @WASI_DIFF_010
  Scenario Outline: An option fog-wasi does not take is refused with its usage
    When fog-wasi runs the module "hello" with the options "<options>"
    Then fog-wasi exits with 2
    And its stderr is "usage: fog-wasi [--env NAME=VALUE]... [--dir HOST::/] module.wasm [arg]...\n"

    Examples:
      | options             | what                         |
      | --env region        | a variable without a value   |
      | --dir /tmp::/data   | a directory not given as "/" |
      | --wasm threads=y    | an option it does not take   |
