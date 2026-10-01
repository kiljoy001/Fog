@libtab_jobs @wire
Feature: A compute job is a collection of ordinary 9P files
  Clients allocate, describe, control, and inspect work using files and text.
  They do not need a runtime-specific RPC client or private 9P opcode.

  @JOB_F01
  Scenario: Opening clone allocates once and partial reads retain that identity
    Given an authorized 9P2000 client with capacity to allocate a job
    When the client opens /compute/clone and reads its ID in several pieces
    Then the pieces form one job ID followed by LF
    And exactly one staging job exists for that open
    And reading the same offsets again returns the same bytes without allocating
    When the client clunks the clone fid
    Then the job remains accessible through its job directory

  @JOB_F02
  Scenario: The job directory exposes the text contract
    Given an allocated staging job
    When a stock 9P client lists and stats its job directory
    Then the directory contains spec, ctl, input, status, and result
    And spec and input are writable only while staging
    And status and result are read-only to the client
    And reading status returns a complete fogstatus-v1 LibTab document

  @JOB_F03 @cluster
  Scenario Outline: One file workflow serves all execution engines
    Given an authorized client and an available compatible <runtime> worker
    And a deterministic fixture workload for that runtime
    When the client allocates a job, seals its spec and input, and writes start to ctl
    Then admission succeeds through standard 9P operations
    And the selected worker executes the frozen workload
    And the client reads the expected terminal status and raw result through 9P
    And no runtime-specific network protocol is used

    Examples:
      | runtime |
      | wasm    |

  @JOB_F04 @fuzz
  Scenario Outline: Control files accept exactly one complete command per write
    Given a staging job with a valid sealed specification
    When the client sends <input> in one ctl write
    Then the server returns a tagged control-syntax error
    And no control command takes effect

    Examples:
      | input                                |
      | start without its final LF           |
      | start followed by LF and cancel plus LF |
      | a command with leading spaces        |
      | a command with an argument           |
      | an unknown command followed by LF    |
      | a command containing NUL             |
      | only the first half of start         |

  @JOB_F05 @property
  Scenario: A status fid reads one snapshot despite job progress
    Given a queued job and an open status fid
    And status will be read using generated positive chunk sizes
    When the worker completes while that status fid is being read
    Then all chunks combine into the original complete queued status snapshot
    And no counters or state fields from another revision are mixed into it
    When the client reopens status
    Then the new snapshot reports the completed revision

  @JOB_F06 @property
  Scenario: Raw results are not interpreted as LibTab cells
    Given a successful job returning generated binary bytes including NUL and the text nil
    When the client reads result with generated offsets and counts
    Then the returned bytes exactly match the corresponding result slices
    And no entity encoding, nil conversion, or text decoding changes the payload

  @JOB_F07
  Scenario: An empty successful result differs from an unfinished result
    Given a job whose successful result will be empty
    When the client reads result before completion
    Then the service returns result-not-ready rather than successful EOF
    When the worker completes successfully and the client reopens result
    Then the result is readable and its length is zero
    And a newly opened status reports succeeded with result_bytes zero

  @JOB_F08 @security
  Scenario: A guessed job ID grants no access
    Given a job owned by one principal
    And another principal without access to that job
    When the second principal attempts to read, write, control, or release the job
    Then authorization denies each operation without disclosing its content
    And the owner's job and result are unchanged

  @JOB_F09
  Scenario: Service errors remain bounded LibTab text
    Given a worker failure containing quotes, newlines, and sensitive internal details
    When the service publishes the failure in status
    Then status parses as one fogstatus-v1 document with a stable error code
    And the message is correctly escaped and within the status size limits
    And stack traces, host paths, credentials, and private input are not disclosed
