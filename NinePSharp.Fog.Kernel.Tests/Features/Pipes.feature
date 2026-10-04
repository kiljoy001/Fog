@fog_kernel_pipes
Feature: Pipes carry bytes between processes as 9front's devpipe does
  pipe(2) returns two descriptors, each readable and writable: what is written to one is read from
  the other. A read returns the bytes of at most one write, keeping the rest for the next read, and
  a write is queued in blocks of at most 64 KiB. A reader waits for data; a writer waits while more
  than 256 KiB is queued ahead of the end of its write. Closing the last descriptor for one end lets
  the other end read what is queued and then end of file, three times, after which reads fail; a
  write toward a closed end fails, and the writer gets the note "sys: write on closed pipe", which
  kills a process that does not catch notes.

  Background:
    Given a booted kernel
    And a process with a pipe

  @FOG_KERNEL_008
  Scenario: Bytes written to either end are read from the other
    When it writes "hello" to the first end
    And it writes "olleh" to the second end
    Then reading the second end gives "hello"
    And reading the first end gives "olleh"

  @FOG_KERNEL_008
  Scenario: pipe returns the lowest free descriptors
    Then the pipe's descriptors are 0 and 1

  @FOG_KERNEL_008
  Scenario: A read returns the bytes of at most one write
    When it writes "ab" to the first end
    And it writes "cd" to the first end
    Then reading the second end gives "ab"
    And reading the second end gives "cd"

  @FOG_KERNEL_008
  Scenario: A read shorter than a write leaves the rest for the next read
    When it writes "hello" to the first end
    Then reading 2 bytes from the second end gives "he"
    And reading the second end gives "llo"

  @FOG_KERNEL_008
  Scenario: A read exactly as long as a write leaves nothing of it for the next read
    When it writes "ab" to the first end
    And it writes "cd" to the first end
    Then reading 2 bytes from the second end gives "ab"
    And reading the second end gives "cd"

  @FOG_KERNEL_008
  Scenario: An empty write is read as an empty read
    When it writes "" to the first end
    And it writes "x" to the first end
    Then reading the second end gives ""
    And reading the second end gives "x"

  @FOG_KERNEL_008
  Scenario: A write larger than 64 KiB is queued in 64 KiB blocks
    When it writes 100000 bytes to the first end
    Then reading 200000 bytes from the second end gives 65536 bytes
    And reading 200000 bytes from the second end gives 34464 bytes

  @FOG_KERNEL_009
  Scenario: A read waits for a write
    When it starts reading the second end
    Then the read is still waiting
    When it writes "late" to the first end
    Then the read gives "late"

  @FOG_KERNEL_009
  Scenario: A waiting read stops when it is cancelled
    When it starts reading the second end
    And the read is cancelled
    Then the read was cancelled

  @FOG_KERNEL_009
  Scenario: A write may leave 256 KiB queued without waiting
    When it starts writing 262144 bytes to the first end
    Then the write finishes having written 262144 bytes

  @FOG_KERNEL_009
  Scenario: A writer waits while more than 256 KiB is queued ahead of the end of its write
    When it starts writing 300000 bytes to the first end
    Then the write is still waiting
    When it reads 200000 bytes from the second end
    Then the write finishes having written 300000 bytes

  @FOG_KERNEL_009
  Scenario: A writer waiting on a full pipe goes on when the reader closes
    When it starts writing 300000 bytes to the first end
    And it closes the second end
    Then the write finishes having written 300000 bytes

  @FOG_KERNEL_010
  Scenario Outline: Closing one end lets the other read what is queued, then end of file
    When it writes "bye" to the <closed> end
    And it closes the <closed> end
    Then reading the <open> end gives "bye"
    And reading the <open> end gives ""

    Examples:
      | closed | open   |
      | first  | second |
      | second | first  |

  @FOG_KERNEL_010
  Scenario: After three reads at end of file, reads fail
    When it closes the first end
    Then reading the second end gives ""
    And reading the second end gives ""
    And reading the second end gives ""
    And reading the second end fails with "i/o on hungup channel"

  @FOG_KERNEL_010
  Scenario: An end stays open while a duplicate of its descriptor is open
    When it duplicates the first end as descriptor 5
    And it closes the first end
    And it starts reading the second end
    Then the read is still waiting
    When it closes descriptor 5
    Then the read gives ""

  @FOG_KERNEL_010
  Scenario: A write toward a closed end kills the writer with a note
    When it closes the second end
    And it forks a child that writes "x" to the first end
    Then the child's wait message is "*init* <pid>: sys: write on closed pipe"
