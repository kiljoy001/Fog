@fog_foundation @cluster
Feature: A configured fog starts without an authentication or membership cycle
  Public user access and internal node authority are separate profiles.

  @FOG_B01 @wire
  Scenario: Runtime channels are available before grain activation
    Given valid local node credentials, enrolled peers and the host bootstrap listeners
    And grain activation and remote grain calls are disabled
    When an enrolled peer uses mutual TLS and attaches to the runtime export
    Then it can open the Orleans channel without any grain call
    And all logical application bytes remain standard 9P after configured AAN reassembly

  @FOG_B02 @security
  Scenario Outline: Node enrollment cannot be claimed through user identity
    Given a connection presenting <credential>
    When it tries to join or open the internal transport export
    Then bootstrap rejects the connection before any Orleans payload is delivered

    Examples:
      | credential                              |
      | a valid user factotum proof             |
      | only an enrolled node name in Tattach   |
      | an unpinned client certificate          |
      | another node's certificate and a changed uname |
      | a valid certificate on the user profile |

  @FOG_B03
  Scenario Outline: Invalid host configuration fails before joining
    Given local configuration with <defect>
    When the host starts
    Then startup fails before cluster membership or guest execution
    And no undeclared provider connection is attempted

    Examples:
      | defect                                  |
      | two configured control nodes            |
      | duplicate enrolled node keys            |
      | missing bootstrap credentials           |
      | no enforceable guest containment        |
      | a non-9P remote membership provider      |
      | a required unsupported reminder provider |
      | an already-owned control-state directory |

  @FOG_B04
  Scenario: Authentication readiness is separate from workload readiness
    Given the user authentication listener is ready but Orleans job services are unavailable
    When an enrolled user completes authentication and attempts the fog data attach
    Then verification can complete without invoking a grain
    And data attachment returns not-ready without admitting a job
    And no anonymous or alternate-protocol fallback is exposed

  @FOG_B05 @wire
  Scenario: The selected membership adapter performs real conditional operations over 9P
    Given the configured Orleans membership adapter and host-level membership service
    When concurrent membership updates use the same expected version
    Then only a version-consistent update commits and the other observes the conflict
    And repeated operation identity cannot apply a second mutation
    And a reader receives a coherent membership snapshot
    And no part of the exchange activates a grain or uses non-9P service traffic

  @FOG_B06
  Scenario: Endpoint discovery does not create live membership
    Given a configured node endpoint whose host is unavailable
    When the node table is queried and work placement is attempted
    Then discovery can return configuration without marking the node ready
    And no execution is assigned without compatible live capacity and a valid lease

  @FOG_B07 @security
  Scenario: TLS validation failure does not downgrade the user profile
    Given a server certificate with the wrong configured identity or pin
    When a user client connects
    Then it fails before sending a factotum proof or 9P application data
    And it does not retry in plaintext or learn a replacement pin from the server
