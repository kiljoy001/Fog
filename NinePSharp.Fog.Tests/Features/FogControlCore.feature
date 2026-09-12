@fog_core
Feature: Host-local control transactions validate before executing
  This exercises the record and transaction core, not a mounted 9P endpoint.

  @FOG_CORE_TX01
  Scenario: A sealed canonical request executes once despite a repeated commit command
    Given a core transaction containing a canonical sealed request
    When its owner writes the commit command twice
    Then the core acknowledges seven bytes each time
    And the fixture effect executes exactly once
    And the retained reply is the original request bytes

  @FOG_CORE_TX02
  Scenario: Duplicate LibTab rows cannot become an accepted partial request
    Given a core transaction containing duplicate physical request rows
    When its owner writes the commit command
    Then the core rejects invalid-request and remains staging
    And the fixture effect has not executed

  @FOG_CORE_TX03
  Scenario: Unsealed input cannot execute an effect
    Given a core transaction containing a canonical unsealed request
    When its owner writes the commit command
    Then the core rejects upload-open and remains staging
    And the fixture effect has not executed
