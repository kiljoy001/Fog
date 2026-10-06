@fog_kernel_renumber
Feature: renumber moves a descriptor to another number in one step, as WASI's fd_renumber does
  Plan 9 has no call for this: dup(2) onto the new number and then close(2) of the old leave a
  moment in which both numbers name the file. renumber moves the descriptor, with its offset and
  open mode, and closes whatever the new number named; the old number is then not open. Moving a
  descriptor onto its own number changes nothing.

  Background:
    Given a booted kernel
    And "/tmp/f" holds "hello"
    And "/tmp/g" holds "other"

  @FOG_KERNEL_031
  Scenario: A renumbered descriptor keeps its file and offset under the new number
    Given the process has "/tmp/f" open as descriptor 0, read 2 bytes from it, and "/tmp/g" open as descriptor 1
    When it renumbers descriptor 0 to 1
    Then reading descriptor 1 gives "llo"
    And reading descriptor 0 fails with "fd out of range or not open"

  @FOG_KERNEL_031
  Scenario: Renumbering onto a pipe's end closes that end
    Given the process has a pipe and "/tmp/f" open as descriptor 2
    When it renumbers descriptor 2 to 0
    Then reading the pipe's second end gives nothing

  @FOG_KERNEL_031
  Scenario: Renumbering a descriptor onto its own number changes nothing
    Given the process has "/tmp/f" open as descriptor 0, read 2 bytes from it, and "/tmp/g" open as descriptor 1
    When it renumbers descriptor 0 to 0
    Then reading descriptor 0 gives "llo"

  @FOG_KERNEL_031
  Scenario Outline: Renumbering fails, changing nothing, from a descriptor that is not open or to a negative number
    Given the process has "/tmp/f" open as descriptor 0, read 2 bytes from it, and "/tmp/g" open as descriptor 1
    When it renumbers descriptor <from> to <to>
    Then the call fails with "fd out of range or not open"
    And reading descriptor 1 gives "other"
    And reading descriptor 0 gives "llo"

    Examples:
      | from | to |
      | 5    | 1  |
      | 0    | -1 |
