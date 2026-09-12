@swarm_transport @wire
Feature: All swarm communication uses the 9P wire protocol
  Orleans continues to route and execute grain calls, but its network messages
  travel as file data inside standard 9P exchanges for the whole logical session.
  TLS and stock AAN are transport layers, not alternative application protocols.

  @SW9P_W01 @cluster
  Scenario Outline: Both Orleans link types negotiate and retain 9P framing
    Given a strict 9P-only swarm using <transport> with all network links recorded
    When a <link> connection carries an Orleans request and its response
    Then an independent recorder decodes the configured transport layers
    And the new logical session's first application request is Tversion
    And the peer returns a compatible Rversion
    And attach, walk, and read-write open complete before Orleans data is accepted
    And all subsequent logical application bytes decode as complete 9P messages
    And Orleans payload bytes occur only in Twrite data or Rread data
    And no parallel native Orleans connection is opened

    Examples:
      | link           | transport          |
      | client-to-silo | direct TLS and 9P  |
      | silo-to-silo   | direct TLS and 9P  |
      | client-to-silo | TLS then AAN and 9P |
      | silo-to-silo   | TLS then AAN and 9P |

  @SW9P_W02 @cluster
  Scenario Outline: Runtime control traffic does not bypass 9P
    Given a strict 9P-only swarm using local state or 9P-backed providers
    And all network links are recorded
    When the swarm performs <activity>
    Then the activity completes through the configured 9P paths
    And every service exchange decodes as 9P after TLS and configured AAN reassembly

    Examples:
      | activity                             |
      | a silo joining the cluster           |
      | a membership liveness probe          |
      | a remote grain directory lookup      |
      | placement of a new remote activation |
      | migration of an existing activation  |

  @SW9P_W03 @security
  Scenario: A native Orleans peer cannot bypass negotiation
    Given a strict 9P-only swarm endpoint
    When a peer sends a native Orleans preamble instead of Tversion in the logical stream
    Then the connection is rejected within the handshake deadline
    And the Orleans runtime receives no payload from that connection
    And no native Orleans fallback listener is made available

  @SW9P_W04 @security
  Scenario: Failure to negotiate 9P never enables protocol fallback
    Given a strict 9P-only Orleans client
    And its destination rejects 9P version negotiation
    When the client attempts to connect
    Then the connection attempt fails with a negotiation error
    And the client does not retry using raw Orleans RPC
    And any later new logical session begins with a fresh 9P negotiation

  @SW9P_W05
  Scenario: The channel remains framed after the Orleans handshake
    Given a negotiated and open Orleans channel
    And the Orleans handshake has completed through that channel
    When the sender emits a second Orleans invocation
    Then the invocation is still carried in 9P file data
    And no socket upgrade or unframed passthrough occurs

  @SW9P_W06
  Scenario: Classic 9P clients need no private transport opcodes
    Given an authorized client that implements only standard 9P2000
    And it uses an explicitly configured direct 9P endpoint
    When it negotiates, attaches, walks to /transport/orleans, and opens read-write
    Then the transport channel is available using standard 9P operations
    And channel setup requires no Orleans-specific 9P message type
    And the client need not load Orleans libraries merely to access the channel
