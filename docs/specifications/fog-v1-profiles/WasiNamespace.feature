@fog_v1_profiles @design
Feature: dotnet-webassembly commands use the virtual namespace through WASI
  Fog provides the Preview 1 bridge; dotnet-webassembly provides the execution engine.
  These acceptance scenarios are pending production bindings.

  @WASI_NS_001 @integration
  Scenario: A compiled command uses a namespace-backed data directory
    Given a pinned dotnet-webassembly worker and explicit read-write preopen /data
    When a WASIp1 command opens, writes, seeks, reads and closes /data/report
    Then the expected bytes are visible through an independent authorized 9P client
    And no Linux directory is opened on behalf of the guest

  @WASI_NS_002 @security @property
  Scenario Outline: Relative path traversal cannot escape directory authority
    Given a directory capability for /data and no authority outside it
    When path_open receives <path>
    Then it fails with NOTCAPABLE without opening an unauthorized resource

    Examples:
      | path                        |
      | /etc/passwd                 |
      | ../secret                   |
      | child/../../secret          |
      | link-to-outside/secret      |

  @WASI_NS_003 @security
  Scenario: Namespace replacement cannot enlarge an existing capability
    Given an opened directory capability and retained file descriptor
    When its mount is replaced or unmounted during an admitted operation
    Then the admitted operation retains its authorized channel
    And fresh lookups authorize each newly selected resource
    And a path prefix alone cannot authorize a replacement provider

  @WASI_NS_004 @property
  Scenario: Requested rights cannot exceed inherited rights
    Given a read-only preopen and a generated requested rights set
    When path_open or fd_fdstat_set_rights requests additional rights
    Then it fails with NOTCAPABLE before any provider mutation
    And reduced rights cannot later be restored by the guest

  @WASI_NS_005
  Scenario Outline: WASI open flags preserve their distinct meanings
    Given an existing writable file containing report data
    When path_open requests <flags>
    Then the result is <result>

    Examples:
      | flags       | result                           |
      | CREATE      | open with existing contents      |
      | CREATE EXCL | EXIST with contents unchanged    |
      | TRUNC       | open with length zero            |
      | DIRECTORY   | NOTDIR with contents unchanged   |

  @WASI_NS_006
  Scenario: Renumber atomically moves one descriptor capability
    Given two descriptors with different files, rights, positions and flags
    When fd_renumber moves the first onto the second
    Then the destination retains the source capability, position and flags
    And the old source number is invalid
    And displaced ownership is released exactly once
    And a same-number renumber changes nothing

  @WASI_NS_007 @property @fuzz
  Scenario: Guest memory is checked before any external file effect
    Given generated iovec tables, byte ranges, counts and result pointers
    When fd_read, fd_write, fd_pread or fd_pwrite validates them
    Then wrapped or out-of-bounds ranges fail before provider IO
    And vector count and cumulative copy size cannot exceed admitted limits
    And valid IO reports actual byte counts including short transfers and EOF

  @WASI_NS_008
  Scenario: Memory growth cannot invalidate the host's next buffer access
    Given a command whose linear memory grows between file calls
    When the next call uses a buffer on a newly allocated page
    Then the bridge reads the current memory base and validates the new range
    And no pointer or Span from a previous call survives asynchronous IO

  @WASI_NS_009
  Scenario: Positioned IO cannot invoke Plan 9's implicit-offset sentinel
    Given a seekable descriptor with a shared position of seven
    When fd_pread requests the all-ones 64-bit offset
    Then it reports an unrepresentable offset error without provider IO
    And the shared position remains seven

  @WASI_NS_010
  Scenario: Directory continuation uses WASI cookies and records
    Given a preopened union directory containing repeated names
    When fd_readdir is called with bounded buffers and returned cookies
    Then it emits WASI dirents in union order including repeated names
    And a short final record fragment and resumption obey Preview 1
    And unsupported or foreign cookies fail without corrupting the cursor
    And no raw 9P stat record is exposed as a WASI dirent

  @WASI_NS_011 @cluster
  Scenario: A suspended provider does not prevent process termination
    Given a WASI file call waiting for an asynchronous resource grain
    When status and exit requests reach the process authority
    Then both requests are processed while provider IO remains pending
    And guest execution waits only on its bounded worker
    And late completion cannot write into disposed or replacement guest memory
    And all retained ownership is drained or recorded for recovery

  @WASI_NS_012 @security
  Scenario: A guest without imports remains subject to the deadline
    Given a valid command looping forever without making host calls
    When its independent supervisor deadline expires
    Then the worker is killed and reaped while the Orleans host remains responsive
    And no fuel counter or CancellationToken is claimed to interrupt the loop

  @WASI_NS_013
  Scenario: Unsupported provider operations are explicit
    Given a provider without atomic append, sync or rename support
    When the guest requests one of those operations
    Then it receives NOTSUP without an emulated successful guarantee
    And copy followed by delete is not reported as atomic rename

  @WASI_NS_014
  Scenario: Trap cleanup does not claim to roll back authorized file effects
    Given a command that wrote an authorized file and then traps
    When the worker terminates
    Then staged job result bytes are not published as successful output
    And completed external file effects remain visible
    And descriptors, pending operations and guest memory follow the cleanup contract
