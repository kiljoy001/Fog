@fog_kernel_notes
Feature: Notes interrupt processes, and /proc posts them, as 9front's postnote and devproc do
  A note is a short message posted to a process: by writing the process's /proc/n/note, by
  writing /proc/n/notepg to post it to every other process of a note group, or by the kernel, as
  a write on a closed pipe posts "sys: write on closed pipe". At most five notes wait for a
  process, and a note of ERRMAX bytes or more is refused. Posting a note interrupts a kernel call
  the process is blocked in, which fails "interrupted"; the note is then delivered as the call
  returns. A process not in a kernel call is given the note as its next call begins, standing in
  for the clock tick on which 9front would have given it, and that call then goes on as if no note
  had come. A process that has set
  a handler with notify is given the note and either continues or leaves the note to its
  default, which ends the process with the note as its exit status; a process with no handler
  ends so at once. A process shares its parent's note group unless rfork(RFNOTEG) gives it a new
  one, and /proc/n/noteid reads the group's number and joins another group of the same user's
  when written. /proc/n/args reads the process's arguments, and when written, replaces them as
  the text followed by what was written in brackets. The kernel binds #p on /proc, as
  /lib/namespace does, and serves args, note, noteid and notepg for each process.

  Background:
    Given a booted kernel whose /bin holds the managed program "hold"

  @FOG_KERNEL_051
  Scenario: The process device is bound on /proc and holds the note files of each process
    Then "/proc" is a directory of device "p"
    And the names in the process's own /proc directory are "args fd note noteid notepg"

  @FOG_KERNEL_051
  Scenario: /proc lists each process by its pid until it has exited
    Given a child blocked reading an empty pipe
    Then /proc lists the process and the child
    When the process writes "kill" to the child's note
    And the child has exited
    Then /proc lists only the process

  @FOG_KERNEL_052
  Scenario: A note interrupts a call the process is blocked in, and a handler that continues sees it
    Given a child that continues after every note, blocked reading an empty pipe
    When the process writes "hangup" to the child's note
    Then the child's read fails with "interrupted"
    And the child's handler saw "hangup"

  @FOG_KERNEL_052
  Scenario: A process that does not catch notes ends with the note as its status
    Given a child blocked reading an empty pipe
    When the process writes "interrupt" to the child's note
    Then the child's wait message is "*init* <pid>: interrupt"

  @FOG_KERNEL_052
  Scenario: A handler that leaves a note to its default ends the process with it
    Given a child that leaves every note to its default, blocked reading an empty pipe
    When the process writes "kill" to the child's note
    Then the child's wait message is "*init* <pid>: kill"
    And the child's handler saw "kill"

  @FOG_KERNEL_052
  Scenario: A child blocked in wait is interrupted by a note
    Given a child that continues after every note, blocked waiting for a child of its own
    When the process writes "hangup" to the child's note
    Then the child's wait fails with "interrupted"

  @FOG_KERNEL_053
  Scenario: A note posted to a process outside a kernel call is delivered as its next call begins
    Given a child that continues after every note, held before its next call
    When the process writes "alarm" to the child's note
    And the child is let go to stat "/"
    Then the child's stat succeeds
    And the child's handler saw "alarm"

  @FOG_KERNEL_053
  Scenario: The call a note waited for goes on undisturbed once the note is delivered
    Given a child that continues after every note, held before its next call
    When the process writes "alarm" to the child's note
    And the child is let go to read 4 bytes of "/dev/zero"
    Then the child's read gives 4 bytes
    And the child's handler saw "alarm"

  @FOG_KERNEL_053
  Scenario: A write on a closed pipe posts a note to the writer, and fails if the writer continues
    Given a child that continues after every note, holding the write end of a pipe whose read end is closed
    When the child writes to the pipe
    Then the child's write fails with "i/o on hungup channel"
    And the child's handler saw "sys: write on closed pipe"

  @FOG_KERNEL_053
  Scenario: A write on a closed pipe ends a writer that does not catch notes
    Given a child holding the write end of a pipe whose read end is closed
    When the child writes to the pipe
    Then the child's wait message is "*init* <pid>: sys: write on closed pipe"

  @FOG_KERNEL_054
  Scenario: At most five notes wait for a process
    Given a child that continues after every note, held before its next call
    When the process writes 6 notes to the child's note
    Then the first 5 writes succeed and the sixth fails with "note not posted"

  @FOG_KERNEL_054
  Scenario: A note of ERRMAX bytes or more is refused
    Given a child that continues after every note, held before its next call
    When the process writes a note of 128 bytes to the child's note
    Then the write fails with "read or write too large"

  @FOG_KERNEL_054
  Scenario: Reading a process's note takes the first note waiting for it
    Given a child that continues after every note, held before its next call
    When the process writes "first" to the child's note
    And the process writes "second" to the child's note
    Then reading the child's note gives "first"
    And reading the child's note gives "second"
    And reading the child's note gives nothing

  @FOG_KERNEL_055
  Scenario: Writing notepg posts the note to every other process of the writer's note group
    Given 2 children that continue after every note, blocked reading an empty pipe
    When the process writes "hangup" to its own notepg
    Then each child's handler saw "hangup"
    And no note waits for the process

  @FOG_KERNEL_055
  Scenario: A process given a new note group by rfork is not reached by its old group's notes
    Given a child in a note group of its own that continues after every note, held before its next call
    When the process writes "hangup" to its own notepg
    Then no note waits for the child
    And the child's note id is not the process's

  @FOG_KERNEL_055
  Scenario: notepg opens for writing only
    When the process opens its own notepg for reading
    Then the call fails with "permission denied"

  @FOG_KERNEL_056
  Scenario: noteid reads the number of the process's note group as the kernel writes numbers
    Given a child held before its next call
    Then the child's noteid reads the process's note id, right-aligned in 11 places and a space

  @FOG_KERNEL_056
  Scenario: Writing a note id of the same user's group joins it, and a process's own pid makes a group of its own
    Given a child in a note group of its own that continues after every note, held before its next call
    When the process writes its own note id to the child's noteid
    Then the child's note id is the process's
    When the process writes the child's pid to the child's noteid
    Then the child's note id is its pid

  @FOG_KERNEL_056
  Scenario Outline: A note id that names no group of the same user's, or no group, is refused
    A note id is read as atoi reads it, and taken as an unsigned number, so a negative one names
    a group no process has.

    Given a child held before its next call
    When the process writes "<id>" to the child's noteid
    Then the write fails with "<error>"

    Examples:
      | id          | error                  |
      | 0           | bad arg in system call |
      | none        | bad arg in system call |
      | 999999      | permission denied      |
      | -1          | permission denied      |
      | 99999999999 | permission denied      |

  @FOG_KERNEL_057
  Scenario: args reads the arguments a process was run with, each quoted as rc quotes it
    Given a child that runs "/bin/hold" with arguments "a 'b c'"
    Then reading the child's args gives "hold a 'b c'"

  @FOG_KERNEL_057
  Scenario: Writing args replaces them with the text and what was written, in brackets
    Given a child that runs "/bin/hold" with arguments "a"
    When the process writes "now waiting" to the child's args
    Then reading the child's args gives "hold [now waiting]"

  @FOG_KERNEL_058
  Scenario: A process of none may post a note to another of none in its own note group, and in no other
    Given a child that becomes "none" and forks a grandchild that continues after every note, held before its next call
    When the child writes "hangup" to the grandchild's note
    Then the child's write succeeds
    And reading the grandchild's note gives "hangup"
    When the child makes a note group of its own
    And the child writes "hangup" to the grandchild's note
    Then the child's write fails with "permission denied"

  @FOG_KERNEL_072
  Scenario: alarm posts the process an alarm note once its time has passed
    Given a child that continues after every note, blocked reading an empty pipe after setting an alarm of 50 milliseconds
    Then the child's read fails with "interrupted"
    And the child's handler saw "alarm"

  @FOG_KERNEL_072
  Scenario: alarm 0 cancels the alarm, giving the milliseconds it had left, and 0 when none is set
    Given a child that continues after every note, held before its next call
    When the child sets alarms of 60000, 0 and 0 milliseconds
    Then the alarms gave 0, nearly 60000 and 0 milliseconds

  @FOG_KERNEL_072
  Scenario: A process forked from one whose alarm is set has none of its own
    Given a child that continues after every note, held before its next call
    When the child sets an alarm of 60000 milliseconds and forks a grandchild that cancels its own
    Then the grandchild's alarm gave 0 milliseconds
