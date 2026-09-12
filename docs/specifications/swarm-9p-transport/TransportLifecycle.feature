@swarm_transport
Feature: 9P transport lifetime is independent of grain lifetime
  The transport can bootstrap the runtime it carries and can terminate safely
  without replaying ambiguous byte streams or retaining stale session state.

  @SW9P_L01
  Scenario: Transport setup does not depend on the grain runtime
    Given the local transport listener and local peer authorization policy are ready
    And grain activation and remote grain calls are disabled by the fixture
    When an authorized peer negotiates and opens an Orleans channel
    Then channel setup succeeds
    And the fixture observes no grain activation or remote grain call during setup
    And closing the channel releases its transport resources without a grain call

  @SW9P_L02
  Scenario Outline: An incomplete handshake has a bounded lifetime
    Given a connection with a configured handshake deadline
    When its peer stalls during <stage>
    And the controlled handshake deadline expires
    Then the connection attempt terminates
    And its buffers, pending requests, and open fids return to the idle baseline

    Examples:
      | stage               |
      | security negotiation |
      | version negotiation |
      | attach authorization |
      | walking the channel |
      | opening the channel |

  @SW9P_L03 @wire
  Scenario: Flush cancels an idle read without waiting for producer data
    Given an open channel with read tag 20 blocked on an idle producer
    When Tflush with tag 21 names oldtag 20
    Then Rflush echoes tag 21 without waiting for producer data
    And no response for the old read appears after Rflush
    And tag 20 can identify a new request after Rflush
    And the channel remains usable

  @SW9P_L04 @property @wire
  Scenario: A racing response cannot cross the flush barrier
    Given a read response and its Tflush are held at controlled competing barriers
    When the fixture releases the barriers in either order
    Then the read response appears before Rflush or is suppressed
    And it never appears after Rflush
    And completing the old request cannot complete a new request reusing its tag

  @SW9P_L05 @wire
  Scenario: Flushing an unknown or completed request still succeeds
    Given a negotiated transport session with no pending request using tag 30
    When Tflush with tag 31 names oldtag 30
    Then the server returns Rflush with tag 31 rather than Rerror
    And unrelated pending requests are unaffected

  @SW9P_L06
  Scenario: Cancellation of an ambiguous stream write terminates the channel
    Given a write whose bytes may have been partly accepted by the peer
    And no definitive acknowledgement has reached the sender
    When the adapter cancels that write
    Then it terminates the affected Orleans byte stream
    And it does not guess an accepted prefix or replay bytes into a replacement stream
    And it does not report that an already dispatched grain mutation was rolled back

  @SW9P_L07
  Scenario Outline: Termination releases blocked operations and channel ownership
    Given an open non-resumable channel with both a blocked read and a backpressured write
    When <termination> occurs
    Then pending operations complete or fail within the configured cleanup deadline
    And transport tasks, channel handles, and owned buffers return to the idle baseline
    And no cleanup operation requires a remote grain call

    Examples:
      | termination                    |
      | the client clunks the channel  |
      | the network disconnects        |
      | the transport aborts           |
      | the host reaches its shutdown deadline |

  @SW9P_L08 @wire
  Scenario: Version renegotiation resets the session rather than the running swarm
    Given a transport session with an open channel and pending IO
    When a valid Tversion starts a new session on that connection
    Then the preceding channel and its pending IO are terminated
    And no preceding-session response appears after the new Rversion
    And old fids are invalid until newly attached and opened
    And other swarm connections continue to operate

  @SW9P_L09 @wire
  Scenario: Reconnection after terminal loss establishes a fresh channel
    Given a logical connection that permanently failed after some stream bytes were acknowledged
    When Orleans requests a replacement connection
    Then the replacement negotiates, attaches, walks, and opens a fresh channel
    And the transport does not replay buffered bytes from the old connection
    And old fid and tag state cannot affect the replacement
    And the new connection performs its own Orleans handshake inside 9P data
