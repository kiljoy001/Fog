Feature: Fog control files over secured standard 9P
  The direct node profile exposes bounded transactions through real file operations.
  This feature does not certify AAN or user factotum authentication.

  Scenario: Fragmented input commits once and releases after reading all outputs
    Given an enrolled node connected to the direct TLS 1.3 control listener
    When the node submits a 3000 byte control request using 256 byte 9P messages
    Then its immutable reply matches all 3000 input bytes
    And the host applied the transaction exactly once
    And no transaction reservation remains after the result is received

  Scenario: An incorrect server identity cannot reach the control export
    Given an enrolled node connected to the direct TLS 1.3 control listener
    When the node connects using an incorrect expected server name
    Then TLS rejects the connection before any transaction effect
