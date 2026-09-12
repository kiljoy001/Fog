@swarm_transport @aan
Feature: AAN preserves a live logical byte stream across temporary carrier loss
  A physical carrier can be replaced without replaying an application operation.
  This requires both logical endpoints and their bounded sequencing state to survive.

  @SW9P_AC01 @property @wire
  Scenario: Partial physical records do not corrupt the logical stream
    Given a live AAN session carrying generated binary data in both directions
    When the fixture cuts the carrier at generated header and payload boundaries
    And the same live endpoints complete authorized resumption before their deadlines
    Then each logical receiver obtains exactly the original bytes in order
    And no partial physical record or retransmitted duplicate reaches its parser
    And each direction stays within its owned byte budget

  @SW9P_AC02 @property @wire
  Scenario: A lost acknowledgement does not repeat a 9P request
    Given a complete 9P write has reached the logical server once
    And the carrier loses its AAN acknowledgement and 9P reply
    When that live session resumes and retransmits unacknowledged records
    Then the client receives the original 9P reply once
    And the logical server dispatch count for the write remains one
    And no new 9P write is synthesized by the reconnecting adapter

  @SW9P_AC03 @wire
  Scenario: One-way traffic releases the replay window
    Given an AAN replay window holding at most ten data records
    And the receiver consumes data but sends no application bytes
    When the sender transfers thirty records while both carriers remain writable
    Then all thirty records reach the logical receiver
    And synchronization acknowledgements release the sender's retained records
    And progress does not require reverse application traffic or an enlarged window

  @SW9P_AC04 @property
  Scenario: Duplicate records still carry useful cumulative acknowledgements
    Given a resumed session receives an already accepted sequence with a valid newer ACK
    When the receiver processes that record
    Then its payload is not delivered again
    And the valid ACK releases only the acknowledged outgoing records
    And a subsequent stale ACK cannot move the release watermark backward

  @SW9P_AC05 @wire
  Scenario: Resumption preserves the existing 9P and Orleans handshakes
    Given a live authenticated AAN session with negotiated msize and an open Orleans channel
    And a 9P message is partially assembled at a logical endpoint
    When its physical carrier fails and the same session resumes
    Then the original message assembly, msize, fids and pending tags are preserved
    And neither endpoint injects Tversion, attach, open or another Orleans handshake
    And subsequent bytes continue the original logical stream

  @SW9P_AC06 @property @wire
  Scenario: Resumption does not cross the flush reply barrier
    Given a 9P reply and its Rflush have a serialized logical order
    When the carrier fails during delivery and resumes with duplicate records
    Then any old reply precedes Rflush or remains suppressed
    And no old reply completes a new request reusing that tag

  @SW9P_AC07
  Scenario Outline: Terminal loss cannot be treated as a resumable break
    Given a suspended AAN session with open handles and unacknowledged bytes
    When <termination> occurs
    Then the endpoint detecting terminal loss fails its pending IO within the cleanup deadline
    And an unaware peer detects failure or expires its own bounded retention deadline
    And each endpoint releases redial tasks, handles and owned buffers after detecting termination
    And any replacement logical session negotiates and authenticates afresh
    And no old bytes, afids, fids or tags enter that replacement session

    Examples:
      | termination                         |
      | the resume deadline expires         |
      | the absolute session lifetime ends  |
      | the client loses its session state  |
      | the server loses its session state  |
      | the caller closes the logical stream |
      | the host reaches its shutdown deadline |

  @SW9P_AC08 @wire
  Scenario: A version reset cannot resurrect preceding handles after resume
    Given a live AAN session in which Tversion has reset the logical 9P epoch
    When the carrier fails and that transport session resumes
    Then no preceding-epoch response appears after the new Rversion
    And preceding-epoch fids remain invalid

  @SW9P_AC09
  Scenario: An operation timeout cannot splice ambiguous queued bytes
    Given a suspended session with a partially transmitted request whose deadline expires
    And the client cannot complete its flush and tag-retirement barrier
    When the client reports the operation timeout
    Then the logical session is terminated before its pending tags can be reused
    And the adapter does not delete a byte range and continue the remaining stream
    And the timeout is not reported as rollback of a dispatched operation

  @SW9P_AC10 @wire
  Scenario: Synchronization is not logical EOF
    Given an idle AAN session with a pending positive-length logical read
    When it receives a valid zero-length synchronization record
    Then the read remains pending rather than returning EOF
    When the local owner explicitly closes the logical stream
    Then closure drains or aborts within its finite deadline
    And physical reconnection cannot reopen that closed stream
