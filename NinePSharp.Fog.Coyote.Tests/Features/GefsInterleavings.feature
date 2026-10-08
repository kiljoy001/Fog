@fog_coyote
Feature: A gefs store's one writer keeps its trees whole in every interleaving Coyote explores
  Every tree served from one store changes it under the store's lock, so changes through different
  trees, commits and snapshots run one at a time. Coyote controls the tasks and the lock, and the
  gefs assembly is rewritten to check data races, so Coyote also switches tasks at its memory and
  collection accesses: a change made outside the lock races where Coyote can run another task
  into it. Each scenario repeats a race many times in different orders, then commits, opens the
  device again and checks what each tree holds and that every block of the device is held, in a
  log or free. Scenarios are skipped unless the assemblies were rewritten with coyote rewrite.

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
