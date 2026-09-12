@swarm_transport @aan @wire
Feature: Stock AAN framing carries 9P beneath all application RPC
  The selected data phase is TCP then TLS then stock AAN records then logical 9P.
  These data-plane scenarios do not replace the secure bootstrap and resume contract.

  @SW9P_AW01
  Scenario: The recorder accounts for every transport and application layer
    Given authorized endpoints have established the selected TLS and AAN data phase
    And an independent recorder can decode the protected fixture traffic
    When a new logical session negotiates and exchanges Orleans messages through its channel
    Then the data phase inside TLS consists only of stock AAN records
    And their reconstructed logical stream begins with Tversion and compatible Rversion
    And every subsequent logical message is standard 9P
    And Orleans payloads occur only inside Twrite data or Rread data
    And no outer 9P service wraps the AAN records or carries a parallel RPC stream

  @SW9P_AW02 @property
  Scenario: AAN payload bounds do not replace negotiated 9P message bounds
    Given a logical session with negotiated msize 16384
    And its AAN record payload maximum is 8192 bytes
    When it exchanges valid 9P messages larger than 8192 bytes at generated record boundaries
    Then every AAN record has the stock 12-byte little-endian header and at most 8192 payload bytes
    And reassembly yields the exact original 9P messages without exceeding msize
    And neither layer assumes that its record boundaries match the other layer's messages

  @SW9P_AW03
  Scenario Outline: Stock record interoperability is checked against an independent peer
    Given a real 9front aan peer with its source revision recorded in a controlled codec fixture
    And the built-in codec is operating as the <role>
    When the fixture exchanges bounded data, interrupts the carrier and replays retained records
    Then both sides decode stock record headers and receive the original logical bytes once
    And no private header fields or modified peer codec are required by the built-in codec
    And observed reference-peer limitations are recorded rather than silently patched away
    And codec success is not reported as production resume authentication or full behavioral conformance

    Examples:
      | role   |
      | client |
      | server |

  @SW9P_AW04 @security
  Scenario: AAN data framing cannot be silently replaced with direct 9P
    Given an authenticated endpoint is in the AAN-required data phase
    When its peer sends bare Tversion bytes without AAN framing
    Then the invalid data phase terminates within its configured deadline
    And no logical 9P request or Orleans invocation is dispatched from those bytes
    And the endpoint does not reinterpret the carrier as a direct 9P connection
