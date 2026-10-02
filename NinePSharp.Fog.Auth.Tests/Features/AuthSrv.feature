@fog_authsrv
Feature: Fog issues dp9ik tickets as 9front authsrv does
  The host is the auth server for its authdom. It answers ticket requests on TCP with the
  semantics of 9front sys/src/cmd/auth/authsrv.c run as "authsrv -N": AuthPAK establishes a
  PAK key per id, and AuthTreq returns form 1 tickets sealed with those keys. Keys come from
  the keyfs database. DES tickets are never issued. The client side is played with the Dp9ik
  package, whose primitives are verified against 9front in dp9ik.net.

  Background:
    Given a keyfs with the users
      | user   | password        |
      | fog    | fog-password    |
      | glenda | glenda-password |
      | scott  | scott-password  |
    And an auth server on that keyfs

  # --- AuthPAK -----------------------------------------------------------------

  @FOG_AUTHSRV_001
  Scenario: A PAK request runs one AuthPAK exchange for the authid and then the hostid
    When a client sends a PAK request with authid "fog", hostid "glenda" and uid "glenda"
    Then the server replies AuthOK
    And the client completes an AuthPAK exchange as "fog" and then as "glenda"
    And the server is ready for the next request

  @FOG_AUTHSRV_001
  Scenario Outline: A PAK request without a hostid runs one exchange for the uid, and one without an authid skips it
    When a client sends a PAK request with authid "<authid>", hostid "<hostid>" and uid "<uid>"
    Then the server replies AuthOK
    And the client completes an AuthPAK exchange as each of "<exchanges>"
    And the server is ready for the next request

    Examples:
      | authid | hostid | uid    | exchanges |
      | fog    |        | glenda | glenda    |
      |        | glenda | glenda | glenda    |

  # --- AuthTreq ------------------------------------------------------------------

  @FOG_AUTHSRV_002
  Scenario: After PAK a ticket request returns a ticket for the host and one for the server
    Given a client that completed a PAK request with authid "fog", hostid "glenda" and uid "glenda"
    When it sends a ticket request with authid "fog", hostid "glenda", uid "glenda" and a fresh challenge
    Then the server replies AuthOK, a form 1 AuthTc ticket and a form 1 AuthTs ticket
    And the AuthTc ticket opens with the PAK key the client shares as "glenda"
    And the AuthTs ticket opens with the PAK key the client shares as "fog"
    And both tickets carry the challenge, client user "glenda", server user "glenda" and the same 32-byte key

  @FOG_AUTHSRV_002
  Scenario: Every ticket request gets a fresh ticket key
    Given a client that completed two PAK and ticket request rounds for "glenda" on one connection
    Then the two ticket keys differ

  @FOG_AUTHSRV_003
  Scenario Outline: A user who cannot authenticate gets an exchange that looks like any other
    Given the keyfs user "glenda" is <state>
    And a client that completed a PAK request with authid "fog", hostid "glenda" and uid "glenda"
    When it sends a ticket request with authid "fog", hostid "glenda", uid "glenda" and a fresh challenge
    Then the server replies AuthOK, a form 1 AuthTc ticket and a form 1 AuthTs ticket
    And the AuthTc ticket does not open with the PAK key the client shares as "glenda"
    And the AuthTs ticket opens with the PAK key the client shares as "fog"
    And the server's public values for "glenda" are not all zeros and differ between two exchanges

    Examples:
      | state                              |
      | unknown                            |
      | disabled                           |
      | expired                            |
      | in purgatory after 10 bad attempts |
      | without an AES key                 |

  @FOG_AUTHSRV_004
  Scenario Outline: A host gets a usable ticket for another user only when it speaks for that user
    Given the auth server's speaks-for rules
      | hostid | uid    |
      | scott  | glenda |
    And a client that completed a PAK request with authid "fog", hostid "<hostid>" and uid "<uid>"
    When it sends a ticket request with authid "fog", hostid "<hostid>", uid "<uid>" and a fresh challenge
    Then the AuthTc ticket <opens> with the PAK key the client shares as "<hostid>"

    Examples:
      | hostid | uid    | opens         |
      | glenda | glenda | opens         |
      | scott  | glenda | opens         |
      | glenda | scott  | does not open |
      | scott  | scott  | opens         |
      | scott  | bootes | does not open |

  @FOG_AUTHSRV_004
  Scenario Outline: A wildcard speaks-for rule yields to an explicit exclusion
    Given the keyfs has the user "bootes" with password "bootes-password"
    And the auth server's speaks-for rules
      | hostid | uid    |
      | bootes | *      |
      | bootes | !scott |
    And a client that completed a PAK request with authid "fog", hostid "bootes" and uid "<uid>"
    When it sends a ticket request with authid "fog", hostid "bootes", uid "<uid>" and a fresh challenge
    Then the AuthTc ticket <opens> with the PAK key the client shares as "bootes"

    Examples:
      | uid    | opens         |
      | glenda | opens         |
      | scott  | does not open |

  @FOG_AUTHSRV_005
  Scenario: A ticket request without a PAK exchange is refused, as DES is disabled
    When a client sends a ticket request with authid "fog", hostid "glenda", uid "glenda" and a fresh challenge
    Then the server replies AuthErr "DES is disabled"
    And the server closes the connection

  @FOG_AUTHSRV_005
  Scenario Outline: A ticket request naming an id without a PAK key is refused
    Given a client that completed a PAK request with authid "fog", hostid "glenda" and uid "glenda"
    When it sends a ticket request with authid "<authid>", hostid "<hostid>", uid "glenda" and a fresh challenge
    Then the server replies AuthErr "DES is disabled"
    And the server closes the connection

    Examples:
      | authid | hostid |
      | scott  | glenda |
      | fog    | scott  |

  @FOG_AUTHSRV_015
  Scenario: A user whose AES key holds a zero byte authenticates
    Given the keyfs has the user "zeroed" with a password whose AES key holds a zero byte
    And a client that completed a PAK request with authid "fog", hostid "zeroed" and uid "zeroed"
    When it sends a ticket request with authid "fog", hostid "zeroed", uid "zeroed" and a fresh challenge
    Then the AuthTc ticket opens with the PAK key the client shares as "zeroed"

  @FOG_AUTHSRV_006
  Scenario: PAK keys serve one ticket request
    Given a client that completed a PAK request with authid "fog", hostid "glenda" and uid "glenda"
    And it sent a ticket request with authid "fog", hostid "glenda", uid "glenda" and a fresh challenge
    When it sends a ticket request with authid "fog", hostid "glenda", uid "glenda" and a fresh challenge
    Then the server replies AuthErr "DES is disabled"

  @FOG_AUTHSRV_007
  Scenario: A ticket request with an empty uid ends the connection
    Given a client that completed a PAK request with authid "fog", hostid "glenda" and uid "glenda"
    When it sends a ticket request with authid "fog", hostid "glenda", uid "" and a fresh challenge
    Then the server closes the connection without replying

  # --- Other requests and the connection ---------------------------------------

  @FOG_AUTHSRV_008
  Scenario Outline: Request types other than AuthPAK and AuthTreq end the connection
    When a client sends a ticket request of type <type>
    Then the server closes the connection without replying

    Examples:
      | type         |
      | AuthChal     |
      | AuthApop     |
      | AuthChap     |
      | AuthMSchap   |
      | AuthMSchapv2 |
      | AuthNTLM     |
      | AuthCram     |
      | AuthVNC      |
      | AuthTs       |

  @FOG_AUTHSRV_009
  Scenario Outline: A client that stops sending part way is disconnected
    When a client <stops>
    Then the server closes the connection
    And the auth server goes on serving other clients

    Examples:
      | stops                                                            |
      | sends 100 of the 141 bytes of a ticket request and stops sending  |
      | sends a PAK request and stops sending before its public value     |

  @FOG_AUTHSRV_009
  Scenario: A PAK exchange with an invalid public value ends the connection
    When a client sends a PAK request with authid "fog", hostid "glenda" and uid "glenda"
    And it answers the server's first public value with an encoding greater than (p-1)/2
    Then the server closes the connection without replying

  @FOG_AUTHSRV_010
  Scenario: A connection is closed when its lifetime ends, as authsrv's alarm does
    Given the auth server closes connections 2 seconds after they open
    When a client connects and sends nothing for 3 seconds
    Then the server has closed the connection

  @FOG_AUTHSRV_011
  Scenario: Connections are served independently and forgotten when they end
    When 5 clients each complete a PAK and ticket request round for "glenda" at the same time
    Then every client receives tickets that open with its own PAK key
    And the auth server tracks 5 connections
    When those clients disconnect
    Then the auth server tracks no connections

  @FOG_AUTHSRV_012
  Scenario: Names longer than the field are terminated as convM2TR does
    Given the keyfs has the user "abcdefghijklmnopqrstuvwxyz0" with password "long-password"
    And a client that completed a PAK request with authid "fog", hostid "abcdefghijklmnopqrstuvwxyz0" and uid "abcdefghijklmnopqrstuvwxyz0"
    When it sends a ticket request whose uid field holds 28 bytes "abcdefghijklmnopqrstuvwxyz0X"
    Then the AuthTc ticket's client user is "abcdefghijklmnopqrstuvwxyz0"

  @FOG_AUTHSRV_013
  Scenario: A key changed in the keyfs is used by the next request
    When the keyfs user "glenda" is given the password "changed-password"
    And a client that completed a PAK request with authid "fog", hostid "glenda" and uid "glenda" using that password
    And it sends a ticket request with authid "fog", hostid "glenda", uid "glenda" and a fresh challenge
    Then the AuthTc ticket opens with the PAK key the client shares as "glenda"

  @FOG_AUTHSRV_014
  Scenario: The auth server listens only on its configured endpoint
    Then the auth server listens on its configured TCP endpoint
    And it stops listening when the host shuts down

  @FOG_AUTHSRV_014
  Scenario: Shutting down closes connected clients
    Given a client that completed a PAK request with authid "fog", hostid "glenda" and uid "glenda"
    When the auth server shuts down
    Then the server closes the connection
