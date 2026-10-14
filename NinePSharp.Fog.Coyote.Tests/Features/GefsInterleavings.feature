@fog_coyote
Feature: A gefs store's one writer keeps its trees whole in every interleaving Coyote explores
  Every tree served from one store changes it through the store's one writer, a task that runs the
  changes one at a time, in the order they were asked for, under the store's lock; changes through
  different trees, snapshots and the first pass of a commit run one at a time. A change queues its
  blocks to be written by the sync task, waiting for room when the queue is full, and a commit
  waits for the blocks queued before it, then writes to the device with the lock released. Reads
  take no lock: each takes a tree's root once, inside an epoch, and a block a commit frees waits in
  limbo until every read that might use it has finished. Coyote controls the tasks and the lock,
  and the gefs assembly is rewritten to check data races, so Coyote also switches tasks at its
  memory and collection accesses: a change made outside the lock, a change run out of turn, a block
  written before its turn, or a block reused under a read, races where Coyote can run another task
  into it. Each scenario repeats a race many times in different orders, then checks what each tree
  holds and that every block of the device is held, in a log or free. Scenarios are skipped unless
  the assemblies were rewritten with coyote rewrite.

  @FOG_COYOTE_201
  Scenario: Writers in two trees of one store, racing a commit, leave every file whole
    Given a store serving main and work, a fork of main
    When 2 writers each create a file in main and 2 in work while another task commits
    Then in every explored schedule, once the device is opened again, main and work each hold their writers' files with what was written, and every block of the device is held, in a log or free

  @FOG_COYOTE_202
  Scenario: Two attaches of a label not yet served serve one tree
    Given a store with main snapshotted as monday
    When 2 tasks attach to monday at once
    Then in every explored schedule both attaches serve the same tree

  @FOG_COYOTE_203
  Scenario: A snapshot racing a write and a commit takes the file as it was before the write or after it
    Given a store serving main, with notes holding "first" committed
    When a writer writes "secnd" to notes while another task snapshots main as monday and a third commits
    Then in every explored schedule, once the device is opened again, monday's notes hold "first" or "secnd", main's hold "secnd", and every block of the device is held, in a log or free

  @FOG_COYOTE_204
  Scenario: Reads racing a writer and its commits each see the file whole, as one root held it
    Given a store serving main, with notes holding 2 blocks of "a" committed
    When 2 readers each read notes while a writer rewrites it as 2 blocks of "b" and then of "c", committing after each
    Then in every explored schedule each read gives 2 blocks of one letter, a, b or c, and once the device is opened again every block of it is held, in a log or free

  @FOG_COYOTE_205
  Scenario: Writes asked for one after another are made in that order, whatever a racing commit does
    Given a store serving main, with notes holding "first" committed
    When a writer asks to write "secnd" and then "third" to notes, without waiting between them, while another task commits
    Then in every explored schedule, once the device is opened again, main's notes hold "third", and every block of the device is held, in a log or free

  @FOG_COYOTE_206
  Scenario: Writers and commits racing through a write queue with room for 2 blocks leave every file whole
    Given a store serving main, writing through a queue with room for 2 blocks
    When 2 writers each write a file of 2 blocks while another task commits twice
    Then in every explored schedule, once the device is opened again, main holds both files with what was written, and every block of the device is held, in a log or free
