@fog_gefs
Feature: Blocks wait in a sync queue, written by its own task in gefs's order, after 9front's gefs
  gefs's enqueue puts a finished block on its arena's sync queue, and the sync process writes it.
  The queue is a heap, ordered by the generation a block was queued in and then by the block's
  place on the device, so blocks waiting together go to the device in order of where they lie. A
  commit raises the generation and queues a fence, which sorts before the blocks of its
  generation; it comes out once every block queued before it is written, and before any queued
  after it. A queued block is held in memory, so it reads back from the queue until it is written.
  A full queue makes the next block wait for room. Once the device fails a write, or the store
  fails, the queue writes nothing more, and a block still in it reads back from it.

  gefs has one sync process unless told otherwise, and so does Fog. Fog's sync task is started
  when a block is queued and runs until the queue is empty, rather than waiting on it. The queue
  holds 4096 blocks unless made smaller, where gefs's holds twice the blocks of its cache; Fog keeps
  no cache besides the blocks waiting in the queue. gefs waits for room as long as it takes; Fog
  waits no longer than the device's patience, and then takes the device as failed.

  @FOG_GEFS_805
  Scenario: Blocks waiting together go to the device in order of where they lie, and those queued after a fence after it
    Given a write queue with room for 8 blocks, on a device of 100 blocks
    When the device holds its next write
    And block 40 is queued, and the device holds it
    And blocks 30 and 20 are queued
    And a fence is queued
    And blocks 10 and 50 are queued
    Then the fence is still waiting
    When the device lets the write go
    Then the fence comes out
    And the device was written at blocks 40, 20, 30, 10 and 50, in that order

  @FOG_GEFS_805
  Scenario: A block that finds the queue full waits for room
    Given a write queue with room for 2 blocks, on a device of 100 blocks
    When the device holds its next write
    And block 40 is queued, and the device holds it
    And blocks 30 and 20 are queued
    And block 10 starts to be queued
    Then block 10 is still waiting to be queued
    When the device lets the write go
    Then block 10 is queued
    When a fence is queued
    Then the fence comes out
    And the device was written at blocks 10, 20, 30 and 40, in some order

  @FOG_GEFS_805
  Scenario: A queued block reads back from the queue until it is written
    Given a write queue with room for 8 blocks, on a device of 100 blocks
    When the device holds its next write
    And block 40 is queued, and the device holds it
    And block 20 is queued
    Then blocks 20 and 40 read back from the queue
    When the device lets the write go
    And a fence is queued
    Then the fence comes out
    And no block reads back from the queue

  @FOG_GEFS_805
  Scenario: A fence on an empty queue comes out at once
    Given a write queue with room for 8 blocks, on a device of 100 blocks
    When a fence is queued
    Then the fence comes out

  @FOG_GEFS_805
  Scenario: Once the store fails, the queue writes nothing more, and its blocks still read back
    Given a write queue with room for 8 blocks, on a device of 100 blocks
    When the device holds its next write
    And block 40 is queued, and the device holds it
    And block 20 is queued
    And the queue is told the store failed with "file system full"
    And the device lets the write go
    And a fence is queued
    Then the fence fails with "file system full"
    And the device was written at blocks 40, in that order
    And block 20 reads back from the queue
