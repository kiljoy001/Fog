@swarm_transport @security
Feature: A universal transport has bounded and authenticated sessions
  A shared protocol must not turn an untrusted peer or a slow consumer into
  unrestricted access, unbounded memory use, or blocked cancellation.

  @SW9P_S01
  Scenario: An attach username cannot impersonate an authorized peer
    Given a deployment requiring authenticated swarm peers
    And an unauthenticated connection claiming an authorized Tattach username
    When the peer attempts to open the Orleans channel
    Then access is rejected
    And no Orleans payload reaches the runtime

  @SW9P_S02
  Scenario: Authentication does not bypass channel authorization
    Given an authenticated peer authorized for workload files but not runtime transport
    When the peer attempts to open /transport/orleans
    Then the server denies that channel
    And the peer gains no runtime transport capability by changing its attach username

  @SW9P_S03 @wire
  Scenario: Transport security encloses the entire 9P session
    Given peers configured to require validated mutual TLS
    When they establish an Orleans channel
    Then neither peer accepts application protocol data before peer validation
    And the new logical stream begins with 9P version negotiation after configured AAN decoding
    And all channel traffic remains inside that protected stream
    And any AAN headers, acknowledgements and payloads are also protected by TLS

  @SW9P_S04
  Scenario: A failed security handshake never downgrades to plaintext
    Given peers configured to require validated mutual TLS
    When one peer presents an untrusted certificate
    Then the connection fails before attach or Orleans payload delivery
    And neither peer retries the connection using plaintext

  @SW9P_S05 @property @wire
  Scenario Outline: Message limits include framing overhead
    Given a server with maximum msize 8192 and minimum msize 256
    When the client proposes msize <proposal> using 9P2000
    Then msize <negotiated> is negotiated
    When the client opens the channel and transfers data larger than msize in both directions
    Then reads and writes account for their complete 9P headers
    And no emitted frame exceeds the negotiated msize

    Examples:
      | proposal   | negotiated |
      | 256        | 256        |
      | 4096       | 4096       |
      | 8192       | 8192       |
      | 4294967295 | 8192       |

  @SW9P_S06 @fuzz @wire
  Scenario Outline: Invalid frames cannot reach the Orleans byte stream
    Given a bounded transport connection appropriate to the message being tested
    When the peer supplies <input>
    Then the connection rejects the malformed input or terminates cleanly
    And no bytes from that input reach the Orleans runtime
    And the connection does not allocate or queue data beyond its configured limits
    When the connection closes if it has not already terminated
    Then connection cleanup returns owned resources to the idle baseline

    Examples:
      | input                                              |
      | a frame size smaller than the 7-byte header         |
      | a frame size larger than the current frame limit    |
      | a frame truncated by network EOF                   |
      | a Twrite count larger than its encoded payload      |
      | a Twrite count smaller than its encoded payload     |
      | an Rread count larger than the outstanding request  |
      | an Rwrite with the tag of an outstanding read       |
      | a response with no outstanding request tag          |

  @SW9P_S07 @fuzz
  Scenario Outline: Invalid fid state grants no stream access
    Given a negotiated transport session
    When the peer attempts <operation>
    Then the server returns a tagged protocol error without delivering stream bytes
    And existing authorized fids remain usable

    Examples:
      | operation                                |
      | reading an unknown fid                   |
      | writing a walked but unopened channel    |
      | writing through a read-only fid          |
      | reusing an allocated fid for attach      |
      | accessing a fid from a closed session    |

  @SW9P_S08 @fuzz
  Scenario: Duplicate outstanding tags cannot misroute responses
    Given an open channel with a request using tag 40 still pending
    When the peer submits a different request using tag 40
    Then the session is terminated as a protocol violation
    And the second request delivers no data to the stream
    And no response can be attributed to the wrong operation

  @SW9P_S09
  Scenario: Slow consumers apply backpressure within an explicit byte budget
    Given a connection configured with a finite queued-payload budget
    And the receiving application is held at a consumption barrier
    When the peer continues offering data beyond that budget
    Then accepted queued payload stays within the configured budget
    And further writes wait for capacity or receive a defined overload failure
    And the sender cannot create an unbounded collection of pending write tasks
    When the application resumes consumption
    Then accepted data is delivered in order without duplication

  @SW9P_S10
  Scenario: Saturation preserves cancellation and cleanup progress
    Given a connection whose data-operation and byte budgets are exhausted
    When the peer flushes a blocked operation and closes the channel
    Then flush and channel cleanup progress within their configured deadlines
    And processing them does not require another data-operation slot
    And unrelated connections retain their own configured capacity

  @SW9P_S11 @fuzz
  Scenario Outline: Session quotas fail closed without consuming extra resources
    Given a transport with its configured <quota> fully occupied
    When a peer requests one more resource of that kind
    Then the request is rejected or the offending connection is closed
    And the number of owned resources does not exceed the configured quota
    And resources already owned by other connections remain valid

    Examples:
      | quota                         |
      | live connections              |
      | fids per session              |
      | outstanding data requests     |

  @SW9P_S12 @property @wire
  Scenario Outline: An unusably small msize cannot create a channel
    Given a server with minimum msize 256
    When the client proposes msize <proposal> using 9P2000
    Then version negotiation is rejected without unsigned arithmetic wraparound
    And no channel or Orleans invocation is created

    Examples:
      | proposal |
      | 0        |
      | 7        |
      | 255      |
