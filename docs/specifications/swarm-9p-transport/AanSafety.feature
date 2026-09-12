@swarm_transport @aan @security
Feature: Resumption cannot bypass authority or retain unlimited session state
  AAN provides continuity, not authentication, lease renewal or unbounded storage.
  These scenarios require the chosen wire profile's real resume authentication.

  @SW9P_AS01 @fuzz
  Scenario Outline: Knowing a session reference is not permission to resume it
    Given a live resumable session owned by an enrolled principal
    When a claimant presents <claim>
    Then no retained payload or logical handle is exposed to that claimant
    And the healthy owner is not evicted
    And the session's lifetime and authority are not extended

    Examples:
      | claim                                      |
      | the session ID without authenticated proof |
      | another user's otherwise valid credentials |
      | a user proof for an internal node session  |
      | a previously accepted resume exchange      |
      | an old fog-auth-v1 afid proof as a resume proof |
      | a claim for a different server boot        |

  @SW9P_AS02 @property
  Scenario: Competing carriers have one serialized owner
    Given two authorized claims for the same suspended session at competing barriers
    When both claims are processed
    Then only one carrier generation owns the session at a time
    And late data, ACKs and callbacks from a fenced carrier cannot modify live state
    And losing claims release their resources without closing the winning carrier

  @SW9P_AS03
  Scenario Outline: Resumption does not revive invalid authority
    Given a suspended session with previously authorized open handles
    When <change> occurs before a valid owner requests resumption
    Then the old session cannot resume or deliver queued operations
    And a newly authenticated session cannot inherit its handles

    Examples:
      | change                                |
      | the original authentication expires   |
      | the active policy epoch changes       |
      | the enrolled owner is explicitly revoked |
      | the endpoint incarnation changes      |

  @SW9P_AS04 @fuzz @property
  Scenario Outline: Invalid records cannot release or inject logical bytes
    Given a bounded AAN session with known send and receive watermarks
    When its authenticated carrier supplies <record>
    Then the invalid record does not advance a watermark or deliver payload
    And the parser terminates the invalid session without exceeding its resource limits
    And unacknowledged bytes are not reported as remotely accepted

    Examples:
      | record                                         |
      | a payload length above the profile maximum     |
      | a cumulative ACK beyond one past the highest offered sequence |
      | a data sequence ahead of the expected sequence |
      | a synchronization marker with a payload        |

  @SW9P_AS05 @property
  Scenario: Sequence exhaustion cannot alias synchronization or old data
    Given a session with counters injected near its profile's sequence limit
    When further writes would cross that limit
    Then the session terminates before using a reserved or wrapped data sequence
    And no old record becomes a newly acceptable record
    And a new logical session cannot inherit the old replay queue

  @SW9P_AS06
  Scenario Outline: Retained state remains bounded during a partition
    Given the configured <budget> is exhausted
    When a peer offers more resources of that kind
    Then backpressure or a bounded admission error prevents exceeding that budget
    And another owner's live session is not evicted to admit the request
    And local abort and cleanup do not require a free application data slot

    Examples:
      | budget                    |
      | per-session owned bytes   |
      | node-wide owned bytes     |
      | sessions per owner        |
      | node-wide retained sessions |
      | pending record metadata   |
      | concurrent carrier attempts |

  @SW9P_AS07 @property
  Scenario: Repeated dial failures cannot extend retention
    Given a suspended session with monotonic resume and absolute lifetime deadlines
    When repeated failed dials, partial handshakes and invalid claims arrive
    Then attempts obey the configured bounded backoff and concurrency limits
    And none restarts either deadline
    And expiry terminates the session and releases its ownership

  @SW9P_AS08
  Scenario: Required AAN cannot silently fall back
    Given an endpoint configured to require the pinned AAN profile
    When its peer supports only direct 9P or an incompatible resume profile
    Then connection establishment fails with a bounded profile error
    And neither endpoint downgrades to plaintext or native Orleans RPC
    And the caller is not told that its connection is resumable

  @SW9P_AS09
  Scenario: User resumption cannot recover another principal's handles
    Given a version-one resumable session bound to user alice
    When the client attempts to establish authority for user bob inside that session
    Then the additional principal is rejected before acquiring any bob-owned handles
    And bob must use a separately authenticated resumable session
    And ordinary direct 9P connections retain their specified attach behavior
