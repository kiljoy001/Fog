@fog_gefs
Feature: A store serves its trees by attach name, commits every five seconds and writes one change at a time, after 9front's gefs
  An attach names the tree it serves: a leading % is dropped, and no name serves main. A label
  that names no tree is refused; Fog does not serve gefs's dump directory. A snapshot is served
  read only, and a write to it is refused before it takes any blocks. Every tree served
  from one store shares one writer, so one change runs at a time across them all. Every five
  seconds the store commits, if anything changed since it last did, as gefs's task process
  syncs. A commit that fails leaves the store read only, as gefs's does: what it holds can still
  be read, nothing more is committed, and a wstat naming nothing asks for no commit. Closing the
  server commits what is left. Snapshots are taken through the server, as gefs's console snap
  command takes them, one change among the rest.

  Background:
    Given a device of 800 blocks reamed with 2 arenas by adm at time 100, served by a server

  @FOG_GEFS_701
  Scenario Outline: An attach name selects the tree it serves
    When adm attaches to "main"
    And adm creates file notes in the root with mode 0644 at time 200
    And adm writes "first" to notes at offset 0 at time 200
    And the server snapshots main as monday
    And adm writes "later" to notes at offset 0 at time 200
    And adm attaches to "<aname>"
    Then reading 100 bytes of notes at offset 0 gives "<text>"

    Examples:
      | aname  | text  |
      |        | later |
      | main   | later |
      | %main  | later |
      | %      | later |
      | monday | first |

  @FOG_GEFS_701
  Scenario: A tree served from a snapshot cannot be written
    When adm attaches to "main"
    And adm creates file notes in the root with mode 0666 at time 200
    And the server snapshots main as monday
    And adm attaches to "monday"
    Then adm writing "x" to notes fails with "file system read only"
    When 5 seconds pass
    Then every block of the device is held, in a log or free

  @FOG_GEFS_701
  Scenario Outline: An attach naming no tree is refused
    Then attaching to "<aname>" fails with "attach -- bad specifier"

    Examples:
      | aname   |
      | tuesday |
      | dump    |
      | %dump   |

  @FOG_GEFS_702
  Scenario: A changed store commits every five seconds, and an unchanged one does not
    When adm attaches to "main"
    And adm creates file notes in the root with mode 0644 at time 200
    And 4 seconds pass
    Then the store is at generation 1
    When 1 second passes
    Then the store is at generation 2
    When 10 seconds pass
    Then the store is at generation 2
    When adm writes "kept" to notes at offset 0 at time 300
    And 5 seconds pass
    Then the store is at generation 3
    When the device is opened again, served by a server
    And adm attaches to "main"
    Then reading 100 bytes of notes at offset 0 gives "kept"

  @FOG_GEFS_702
  Scenario: Closing the server commits what changed since the last commit
    When adm attaches to "main"
    And adm creates file notes in the root with mode 0644 at time 200
    And adm writes "kept" to notes at offset 0 at time 200
    And the server is closed
    And the device is opened again, served by a server
    And adm attaches to "main"
    Then reading 100 bytes of notes at offset 0 gives "kept"
    And every block of the device is held, in a log or free

  @FOG_GEFS_703
  Scenario: Trees served from one store take turns writing
    When the server forks main as work
    And 8 users each write 20 files at once, half through "main" and half through "work"
    And the server is closed
    And the device is opened again, served by a server
    Then main and work each hold the files written through them
    And every block of the device is held, in a log or free

  @FOG_GEFS_704
  Scenario Outline: A commit that fails leaves the store read only, still reading what it holds
    When adm attaches to "main"
    And adm creates directory usr in the root with mode d0775 at time 200
    And adm creates file notes in usr with mode 0666 at time 200
    And adm writes "kept" to usr/notes at offset 0 at time 200
    And 5 seconds pass
    And adm creates file draft in usr with mode 0666 at time 300
    And the device fails every write
    And 5 seconds pass
    Then <action> fails with "file system read only"
    And the server's failure is "device write failed"
    And reading 100 bytes of usr/notes at offset 0 gives "kept"
    And usr lists draft and notes
    When adm sets nothing of usr/notes
    Then no write reaches the device when 10 more seconds pass

    Examples:
      | action                                        |
      | adm creating file x in usr                    |
      | the server creating x in the root             |
      | adm writing "x" to usr/notes                  |
      | adm opening usr/notes with truncation         |
      | adm removing usr/draft                        |
      | adm setting the mode to 0600 of usr/notes     |
      | the server committing                         |

  @FOG_GEFS_702
  Scenario: A store made in a file is there when the file is opened again
    Given a store made in a new file of 800 blocks with 2 arenas by adm
    When adm attaches to "main"
    And adm creates file notes in the root with mode 0644 at time 200
    And adm writes "kept" to notes at offset 0 at time 200
    And the server is closed
    And the file is opened again
    And adm attaches to "main"
    Then reading 100 bytes of notes at offset 0 gives "kept"

  @FOG_GEFS_705
  Scenario: A kernel boots on a store's main, and finds its files there when booted again
    When adm attaches to "main"
    And a kernel boots on it for adm
    And the kernel's first process writes "hello" to "/tmp/notes"
    And 5 seconds pass
    And the device is opened again, served by a server
    And adm attaches to "main"
    And a kernel boots on it for adm
    Then the kernel's first process reads "hello" from "/tmp/notes"
