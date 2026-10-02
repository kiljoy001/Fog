@fog_authfid
Feature: A 9P export authenticates attaches with dp9ik as 9front's lib9p and factotum do
  Tauth opens an authentication fid on which the server speaks factotum's p9any server role,
  offering only dp9ik for its auth domain, under the key the keyfs holds for its auth id.
  Reads and writes on the afid are lib9p's authread and authwrite; Tattach with that afid follows
  authattach. The ticket's client user becomes the attach's user. An attach without an afid goes
  to the export underneath unchanged. Where lib9p leaves an error to the file server, the export
  uses lib9p's own message for a server without that operation. Where lib9p's text is whatever
  errstr held before, the export says what happened: "no uname" for Tauth without a uname,
  "rpc too small" for a message factotum would ask more bytes for, and "authentication already
  done" for a write to a finished afid.

  Background:
    Given a keyfs with the users
      | user   | password        |
      | fog    | fog-password    |
      | glenda | glenda-password |
    And an auth server on that keyfs
    And an export that authenticates as "fog" in the auth domain "fog.example"

  @FOG_AUTHFID_001
  Scenario: A user authenticates an afid with dp9ik and attaches as themselves
    When "glenda" authenticates afid 1 for "/" with password "glenda-password"
    And the client attaches fid 0 with afid 1 as "glenda" to "/"
    Then the attach succeeds as the user "glenda"

  @FOG_AUTHFID_001
  Scenario: Tauth returns an auth qid
    When the client sends Tauth for afid 1 as "glenda" to "/"
    Then the reply is Rauth with a QTAUTH qid

  @FOG_AUTHFID_002
  Scenario Outline: The server offers dp9ik for its auth domain only, without p9any's v.2 prefix
    Given the client sent Tauth for afid 1 as "glenda" to "/"
    When the client reads <count> bytes from afid 1
    Then it reads "dp9ik@fog.example" and a NUL byte

    Examples:
      | count |
      | 18    |
      | 128   |

  @FOG_AUTHFID_002
  Scenario: The PAK request names the server and its domain, with the uid left for the client
    Given the client sent Tauth for afid 1 as "glenda" to "/" and read the offer
    And the client chose "dp9ik fog.example" on afid 1
    When the client writes a challenge to afid 1 and reads the PAK request
    Then the request is an AuthPAK ticket request from "fog" in "fog.example" with no host or user id
    And a 56-byte PAK public value follows it

  @FOG_AUTHFID_003
  Scenario Outline: A choice the server cannot speak is refused
    Given the client sent Tauth for afid 1 as "glenda" to "/" and read the offer
    When the client writes "<choice>" and a NUL byte to afid 1
    Then the reply is Rerror "<error>"

    Examples:
      | choice                | error                                             |
      | p9sk1 fog.example     | negotiation failed, no common protocols or keys   |
      | dp9ik other.example   | negotiation failed, no common protocols or keys   |
      | dp9ik                 | invalid argument                                  |
      | dp9ik fog.example x   | invalid argument                                  |

  @FOG_AUTHFID_003
  Scenario: A choice without its NUL byte is too small
    Given the client sent Tauth for afid 1 as "glenda" to "/" and read the offer
    When the client writes "dp9ik fog.example" without a NUL byte to afid 1
    Then the reply is Rerror "rpc too small"

  @FOG_AUTHFID_004
  Scenario Outline: A ticket the server cannot accept is refused, and the exchange can continue
    Given "glenda" has run dp9ik on afid 1 as far as the ticket with password "glenda-password"
    When the client writes <ticket> to afid 1
    Then the reply is Rerror "<error>"
    When the client writes its ticket and authenticator to afid 1
    And the client reads the server's authenticator from afid 1
    Then the server's authenticator answers the client's challenge

    Examples:
      | ticket                                                 | error                      |
      | a ticket sealed with another key                       | auth server protocol botch |
      | its ticket with an authenticator for another challenge | auth server protocol botch |
      | its ticket truncated to 100 bytes                      | rpc too small              |

  @FOG_AUTHFID_004
  Scenario Outline: A short ticket asks for more by its form1 signature, as convM2T does
    Given the client plays the auth server for "fog" with password "fog-password" on afid 1 as "glenda"
    When the client writes <length> bytes beginning "<signature>" to afid 1
    Then the reply is Rerror "<error>"

    Examples:
      | length | signature | error                      |
      | 191    | form1 PR  | rpc too small              |
      | 191    | form1 Ts  | rpc too small              |
      | 191    | form1 Tc  | rpc too small              |
      | 191    | form1 As  | rpc too small              |
      | 191    | form1 Ac  | rpc too small              |
      | 191    | form1 Tp  | rpc too small              |
      | 191    | form1 Hr  | rpc too small              |
      | 191    | form1 XX  | auth server protocol botch |
      | 85     | form1 XX  | auth server protocol botch |
      | 84     | form1 XX  | rpc too small              |

  @FOG_AUTHFID_004
  Scenario Outline: The server takes only a form 1 AuthTs ticket and an AuthAc authenticator for its challenge
    Given the client plays the auth server for "fog" with password "fog-password" on afid 1 as "glenda"
    When the client writes <ticket> to afid 1
    Then the reply is <reply>

    Examples:
      | ticket                                                         | reply                               |
      | a form 1 AuthTs ticket for "glenda" and an AuthAc authenticator | Rwrite                              |
      | a form 1 AuthTc ticket for "glenda" and an AuthAc authenticator | Rerror "auth server protocol botch" |
      | a form 0 AuthTs ticket for "glenda" and an AuthAc authenticator | Rerror "auth server protocol botch" |
      | a form 1 AuthTs ticket for another challenge                    | Rerror "auth server protocol botch" |
      | a form 1 AuthTs ticket and an AuthAs authenticator              | Rerror "auth server protocol botch" |
      | a form 1 AuthTs ticket and an authenticator for another challenge | Rerror "auth server protocol botch" |

  @FOG_AUTHFID_004
  Scenario: A ticket the client made as the auth server authenticates its client user
    Given the client plays the auth server for "fog" with password "fog-password" on afid 1 as "glenda"
    When the client writes a form 1 AuthTs ticket for "glenda" and an AuthAc authenticator to afid 1
    And the client reads the server's authenticator from afid 1
    And the client attaches fid 0 with afid 1 as "glenda" to "/"
    Then the attach succeeds as the user "glenda"

  @FOG_AUTHFID_004
  Scenario Outline: Short or broken exchange messages are refused
    Given the client sent Tauth for afid 1 as "glenda" to "/" and read the offer
    And the client chose "dp9ik fog.example" on afid 1
    When <request>
    Then the reply is Rerror "<error>"

    Examples:
      | request                                                              | error                      |
      | the client writes a 7-byte challenge to afid 1                       | rpc too small              |
      | the client writes a challenge, reads the PAK request and writes 55 bytes | rpc too small          |
      | the client writes a challenge, reads the PAK request and writes an encoding greater than (p-1)/2 | auth server protocol botch |

  @FOG_AUTHFID_004
  Scenario: A wrong password gets tickets the client cannot open
    When "glenda" tries dp9ik on afid 1 with password "wrong-password"
    Then the client cannot open its ticket

  @FOG_AUTHFID_005
  Scenario: A ticket for another user cannot authenticate the afid's uname
    When "glenda" authenticates afid 1 for "/" as the uname "fog" with password "glenda-password"
    And the client reads 0 bytes from afid 1
    Then the reply is Rerror "auth uname mismatch"

  @FOG_AUTHFID_006
  Scenario Outline: Attach follows lib9p's authattach
    Given "glenda" authenticated afid 1 for "/" with password "glenda-password"
    And the client has an attached fid 5 without an afid
    When the client attaches fid 0 with afid <afid> as "<uname>" to "<aname>"
    Then the reply is Rerror "<error>"

    Examples:
      | afid | uname  | aname | error                              |
      | 2    | glenda | /     | unknown fid                        |
      | 5    | glenda | /     | not an auth fid                    |
      | 1    | fog    | /     | auth uname mismatch: glenda vs fog |
      | 1    | glenda | /n    | auth aname mismatch: / vs /n       |

  @FOG_AUTHFID_006
  Scenario: An attach with an unfinished afid is refused as lib9p's zero-count authread
    Given the client sent Tauth for afid 1 as "glenda" to "/"
    When the client attaches fid 0 with afid 1 as "glenda" to "/"
    Then the reply is Rerror "authread count too small"

  @FOG_AUTHFID_006
  Scenario: An attach with a finished afid checks the ticket's user
    Given "glenda" authenticated afid 1 for "/" as the uname "fog" with password "glenda-password"
    When the client attaches fid 0 with afid 1 as "fog" to "/"
    Then the reply is Rerror "auth uname mismatch"

  @FOG_AUTHFID_006
  Scenario: An attach without an afid goes to the export underneath
    When the client attaches fid 0 without an afid as "node-1" to "/"
    Then the attach succeeds without an authenticated user

  @FOG_AUTHFID_007
  Scenario: An authenticated afid serves repeated attaches until it is clunked
    Given "glenda" authenticated afid 1 for "/" with password "glenda-password"
    When the client attaches fid 0 with afid 1 as "glenda" to "/"
    And the client attaches fid 2 with afid 1 as "glenda" to "/"
    Then both attaches succeed as the user "glenda"
    When the client clunks fid 1
    And the client attaches fid 3 with afid 1 as "glenda" to "/"
    Then the reply is Rerror "unknown fid"

  @FOG_AUTHFID_007
  Scenario: Tversion ends every afid on the connection
    Given "glenda" authenticated afid 1 for "/" with password "glenda-password"
    When the client sends Tversion again
    And the client attaches fid 0 with afid 1 as "glenda" to "/"
    Then the reply is Rerror "unknown fid"

  @FOG_AUTHFID_007
  Scenario: Closing the connection ends its afids and tells the export underneath
    Given "glenda" authenticated afid 1 for "/" with password "glenda-password"
    When the connection closes
    And the client attaches fid 0 with afid 1 as "glenda" to "/"
    Then the reply is Rerror "unknown fid"
    And the export underneath saw the connection close

  @FOG_AUTHFID_007
  Scenario: Afids belong to their connection
    Given "glenda" authenticated afid 1 for "/" with password "glenda-password"
    When another connection attaches fid 0 with afid 1 as "glenda" to "/"
    Then the reply is Rerror "unknown fid"

  @FOG_AUTHFID_008
  Scenario Outline: Afids share the connection's fid space
    Given the client sent Tauth for afid 1 as "glenda" to "/"
    And the client has an attached fid 5 without an afid
    When <request>
    Then the reply is Rerror "duplicate fid"

    Examples:
      | request                                                  |
      | the client sends Tauth for afid 1 as "glenda" to "/"     |
      | the client sends Tauth for afid 5 as "glenda" to "/"     |
      | the client attaches fid 1 without an afid as "node-1" to "/" |
      | the client walks fid 5 to fid 1                          |

  @FOG_AUTHFID_008
  Scenario Outline: The export underneath's fids are tracked as lib9p's fid pool holds them
    Given the client has an attached fid 5 without an afid
    When <request>
    And the client sends Tauth for afid <afid> as "glenda" to "/"
    Then the reply is <reply>

    Examples:
      | request                                            | afid | reply                |
      | the client walks fid 5 to fid 6 through "a"        | 6    | Rerror "duplicate fid" |
      | the client walks fid 5 to fid 6 through "a/missing" | 6   | Rauth                |
      | the client clunks fid 5                            | 5    | Rauth                |
      | the client removes fid 5                           | 5    | Rauth                |

  @FOG_AUTHFID_008
  Scenario: A clunked afid number can be used again
    Given the client sent Tauth for afid 1 as "glenda" to "/"
    When the client clunks fid 1
    And the client sends Tauth for afid 1 as "glenda" to "/"
    Then the reply is Rauth with a QTAUTH qid

  @FOG_AUTHFID_009
  Scenario Outline: An afid is not a file
    Given the client sent Tauth for afid 1 as "glenda" to "/"
    When <request>
    Then the reply is Rerror "<error>"

    Examples:
      | request                                      | error                  |
      | the client walks fid 1 to fid 2 through "a"  | cannot clone open fid  |
      | the client walks fid 1 to fid 2              | cannot clone open fid  |
      | the client opens fid 1                       | 9P protocol botch      |
      | the client creates "a" in fid 1              | 9P protocol botch      |
      | the client stats fid 1                       | stat prohibited        |
      | the client wstats fid 1                      | wstat prohibited       |
      | the client removes fid 1                     | remove prohibited      |

  @FOG_AUTHFID_009
  Scenario: Removing an afid clunks it, as lib9p's remove does
    Given the client sent Tauth for afid 1 as "glenda" to "/"
    When the client removes fid 1
    And the client attaches fid 0 with afid 1 as "glenda" to "/"
    Then the reply is Rerror "unknown fid"

  @FOG_AUTHFID_010
  Scenario Outline: Reads and writes out of turn are refused as factotum and lib9p refuse them
    Given the client sent Tauth for afid 1 as "glenda" to "/"
    When <request>
    Then the reply is Rerror "<error>"

    Examples:
      | request                                                | error                                                                |
      | the client writes "dp9ik fog.example" and a NUL byte to afid 1 | phase error protocol phase error: write in state SHaveProtos |
      | the client reads 4 bytes from afid 1                   | authread count too small                                             |

  @FOG_AUTHFID_010
  Scenario: A read the client's count cannot hold still consumes the message, as lib9p's authread does
    Given the client sent Tauth for afid 1 as "glenda" to "/"
    And the client read 4 bytes from afid 1
    When the client reads 128 bytes from afid 1
    Then the reply is Rerror "authrpc botch"

  @FOG_AUTHFID_010
  Scenario: A read where the server waits for a write is refused
    Given the client sent Tauth for afid 1 as "glenda" to "/" and read the offer
    When the client reads 128 bytes from afid 1
    Then the reply is Rerror "authrpc botch"

  @FOG_AUTHFID_010
  Scenario: A finished afid reads as empty and refuses writes
    Given "glenda" authenticated afid 1 for "/" with password "glenda-password"
    When the client reads 128 bytes from afid 1
    Then it reads 0 bytes
    When the client writes "more" to afid 1
    Then the reply is Rerror "authentication already done"

  @FOG_AUTHFID_011
  Scenario Outline: Without a usable server key the server offers nothing, and lib9p's authread says only that factotum failed
    Given the keyfs user "fog" is <state>
    And the client sent Tauth for afid 1 as "glenda" to "/"
    When the client reads 128 bytes from afid 1
    Then the reply is Rerror "authrpc botch"

    Examples:
      | state    |
      | disabled |
      | expired  |
      | unknown  |

  @FOG_AUTHFID_011
  Scenario: Tauth needs a uname
    When the client sends Tauth for afid 1 as "" to "/"
    Then the reply is Rerror "no uname"
