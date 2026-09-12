@swarm_transport @cluster
Feature: A real swarm works with only 9P network paths
  Wire conformance must accompany successful distributed behavior. Local calls,
  mocked grains, and an external gateway alone cannot prove this integration.

  @SW9P_I01 @wire
  Scenario: An external Orleans client reaches a grain on another silo
    Given two real silos and an external Orleans client using only advertised 9P endpoints
    And a caller grain on silo A and a target grain forced onto silo B
    And undeclared outbound connections are blocked and recorded
    When the external client asks the caller to invoke the target with a binary payload
    Then the target executes on silo B and returns the expected result
    And both the client-to-silo and silo-to-silo paths are observed carrying 9P
    And no non-9P swarm connection is attempted

  @SW9P_I02 @wire
  Scenario: Placement changes retain the logical grain reference and the 9P transport
    Given a real two-silo swarm and a client holding a reference to a grain on silo A
    When that grain is migrated to silo B
    And the client invokes it through the same logical reference
    Then the invocation executes on silo B
    And the reply contains the expected result
    And every network path used during migration and invocation uses 9P

  @SW9P_I03 @wire
  Scenario: A lost reply does not become a transport-level exactly-once promise
    Given a remote grain operation with an explicit application operation identity
    And the grain has applied the mutation
    When the logical session permanently fails before its result reaches the caller
    Then the caller receives a failure with an uncertain operation outcome
    And the transport does not report that the mutation was undone
    When an idempotent provider receives an application retry with the same identity
    Then it returns the recorded result without applying a second mutation
    And transport code has not replayed the preceding connection's raw bytes

  @SW9P_I04 @wire
  Scenario: Existing resource IO uses the same protocol as the runtime channel
    Given a real swarm exposing authorized workload resources and runtime channels through 9P
    When a stock 9P client attaches, walks, opens, reads, writes, stats, and clunks a test resource
    Then the resource operations retain the existing namespace and fid semantics
    And the operations reach their owning resource grain
    And any internal cross-silo work is carried over 9P as well

  @SW9P_I05 @wire
  Scenario: A stock 9P client can discover and use the transport file
    Given an authorized stock 9P2000 client independent of the new Orleans adapter
    And the transport consumer is replaced by a deterministic byte echo fixture
    When the client lists and stats /transport/orleans and opens it read-write
    And it writes a binary payload and reads the echoed bytes
    Then directory entries and stat identify a non-directory channel file
    And the echoed payload is byte-for-byte identical
    And no proprietary negotiation or private 9P opcode is required

  @SW9P_I06 @security
  Scenario Outline: Strict deployment rejects a non-9P remote provider before joining
    Given strict 9P-only deployment is enabled
    And <provider> would open a non-9P remote connection
    When the host validates and starts the swarm
    Then startup fails before the node joins the swarm
    And the failure identifies the incompatible provider and its protocol
    And no non-9P connection is attempted by that provider

    Examples:
      | provider                  |
      | membership storage        |
      | grain persistence         |
      | reminder storage          |
      | a distributed stream backplane |
      | network telemetry export  |

  @SW9P_I07 @wire
  Scenario: A disconnected silo cannot strand transport cleanup
    Given a real two-silo swarm with an active cross-silo call
    When the destination silo is stopped abruptly
    Then the call terminates with a result or a failure within its configured deadline
    And failed-connection resources return to the idle baseline
    And the surviving silo continues serving unrelated calls
    And any replacement connections begin with fresh 9P negotiation
