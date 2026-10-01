@fog_foundation @remote_names @design
Feature: Remote hosts are reached by name under /n
  Walking /n/{name} resolves the name and imports that host's root over 9P, so an
  application on another host is /n/{name}/mnt/{app}. Names are DNS domains or Emercoin
  EmerDNS names (.emc, .coin). Every resolver returns one record format that binds an
  address to a key; an address alone is never trusted. This is a later slice than
  NamespaceViews.feature. These scenarios require executable bindings before they count as
  implemented.

  @FOG_NAME_001
  Scenario Outline: The name's top-level domain selects the resolver
    When a principal walks to /n/<name>
    Then the name is resolved through <resolver>

    Examples:
      | name            | resolver                      |
      | somedomain.com  | DNS                           |
      | someapp.emc     | Emercoin name-value storage   |
      | someapp.coin    | Emercoin name-value storage   |

  @FOG_NAME_002
  Scenario: A record must bind a key
    Given a name whose record has an address but no key pin
    When a principal walks to it
    Then the walk reports not found and nothing is dialled

  @FOG_NAME_003
  Scenario: The connection must prove the record's key
    Given a resolved record with an address and a key pin
    When the host at that address presents a different key
    Then the import fails and nothing is mounted at /n/{name}

  @FOG_NAME_004
  Scenario: A key change is a new identity
    Given an imported name whose record later changes its key
    When the name is walked again
    Then the new key is a new import
    And existing fids are not moved to it

  @FOG_NAME_005
  Scenario: A remote host authorizes its own resources
    Given an imported host
    When a principal opens /n/{name}/mnt/{app}/file
    Then the remote host decides using its own policy
    And local grants confer nothing on the remote host

  @FOG_NAME_006
  Scenario: An unresolvable name looks absent
    When a principal walks to /n/unknown.emc
    Then the walk reports not found
