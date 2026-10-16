@fog_kernel_mount
Feature: mount speaks 9P to a file server over a descriptor, as 9front's devmnt does
  mount(fd, afd, old, flag, aname) attaches to the file server on the other end of a descriptor
  and binds what it attached on old, then closes the descriptor. The first mount on a connection
  sends Tversion, offering 32768 bytes of data a message; every process's calls through mounts on
  that connection share it. Each request has a tag of its own, and one process at a time reads the
  replies, handing each to the request it answers, as devmnt's mountmux does. Reads and writes go
  in pieces no larger than what the open file's iounit allows, and a file's stat shows device M.
  An error from the server is the call's error. A server that hangs up fails every call still
  waiting with "mount rpc error", and every later call with "i/o on hungup channel", as its request
  cannot be written. A call cancelled while it waits is flushed, and the mount goes on.

  Fog's channels name a file by its qid, so the driver does not hold a fid for each channel as
  devmnt does. An open file keeps a fid of its own until it is closed. For the files it has only
  walked to, the driver keeps the names leading to each from the attached root and at most a set
  number of fids, letting the oldest go; a file whose fid was let go is walked to again by its
  names, and if what is there now is another file, the file no longer exists.

  Background:
    Given a booted kernel
    And a 9P file server whose tree holds "notes" with "hello" and an empty directory "docs"
    And the process has the client end of a connection to it as descriptor 3

  @FOG_KERNEL_043
  Scenario: Mounting a file server makes its files the namespace's
    When the process mounts descriptor 3 on "/n"
    Then reading "/n/notes" gives "hello"
    And the names in "/n" are "docs notes"

  @FOG_KERNEL_043
  Scenario: The descriptor is closed once it is mounted
    When the process mounts descriptor 3 on "/n"
    Then descriptor 3 is not open

  @FOG_KERNEL_043
  Scenario: A file created through the mount is the server's
    When the process mounts descriptor 3 on "/n" with creation
    And the process creates "/n/docs/new" holding "made"
    Then the server's "docs/new" holds "made"

  @FOG_KERNEL_044
  Scenario: A mounted file stats as the mount driver's
    When the process mounts descriptor 3 on "/n"
    Then "/n/notes" is a file of device "M"
    And "/n/docs" is a directory of device "M"

  @FOG_KERNEL_045
  Scenario: The server's error is the call's error
    Given the server refuses to open "notes" with "no way"
    When the process mounts descriptor 3 on "/n"
    And the process opens "/n/notes" for reading
    Then the call fails with "no way"

  @FOG_KERNEL_046
  Scenario: A write and a read larger than a message go in pieces
    When the process mounts descriptor 3 on "/n" with creation
    And the process writes 100000 bytes to "/n/docs/big"
    Then reading "/n/docs/big" gives the same 100000 bytes
    And no message on the connection carried more than 32768 bytes of data

  @FOG_KERNEL_047
  Scenario: Processes reading through one mount at once are each answered
    When the process mounts descriptor 3 on "/n"
    And 20 children each read "/n/notes" at once
    Then each child read "hello"
    And the connection carried one Tversion

  @FOG_KERNEL_048
  Scenario: A read cancelled while the server holds it is flushed, and the mount goes on
    Given the server holds reads of "notes" until they are released
    When the process mounts descriptor 3 on "/n"
    And the process starts reading "/n/notes"
    And the read is cancelled
    Then the read was cancelled
    And the connection carried a Tflush for the read
    And once reads are released, reading "/n/notes" gives "hello"

  @FOG_KERNEL_049
  Scenario: The driver holds no more fids than its bound for files it has only walked to
    Given the server's tree holds the files "f1" to "f10"
    And the mount driver holds at most 4 fids for walked files
    When the process mounts descriptor 3 on "/n"
    And the process stats "/n/f1" to "/n/f10"
    Then the server holds at most 5 fids
    And stating "/n/f1" again gives the file "f1"

  @FOG_KERNEL_049
  Scenario: A file replaced on the server after its fid was let go no longer exists
    Given the server's tree holds the files "f1" to "f10"
    And the mount driver holds at most 4 fids for walked files
    When the process mounts descriptor 3 on "/n"
    And the process changes to "/n/docs"
    And the process stats "/n/f1" to "/n/f10"
    And the server's "docs" is replaced by a new directory
    Then stating "." fails with "file does not exist"

  @FOG_KERNEL_049
  Scenario: An open file keeps its fid until it is closed
    Given the server's tree holds the files "f1" to "f10"
    And the mount driver holds at most 4 fids for walked files
    When the process mounts descriptor 3 on "/n"
    And the process opens "/n/notes" for reading as descriptor 0
    And the process stats "/n/f1" to "/n/f10"
    Then reading descriptor 0 gives "hello"

  @FOG_KERNEL_050
  Scenario: A call made after the server hangs up cannot be written
    When the process mounts descriptor 3 on "/n"
    And the server hangs up
    Then reading "/n/notes" fails with "i/o on hungup channel"

  @FOG_KERNEL_050
  Scenario: A call waiting when the server hangs up fails with mount rpc error
    Given the server holds reads of "notes" until they are released
    When the process mounts descriptor 3 on "/n"
    And the process starts reading "/n/notes"
    And the server hangs up
    Then the read fails with "mount rpc error"
