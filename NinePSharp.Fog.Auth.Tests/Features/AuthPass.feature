@fog_authpass
Feature: Users change their password and secret as 9front auth/passwd does
  The auth server answers AuthPass as 9front authsrv's changepasswd does, for auth/passwd:
  after an AuthPAK exchange for the uid alone, it sends an AuthTp ticket under that PAK key and
  takes password requests sealed with the ticket until one is accepted. A refusal names the
  client's address, as authsrv's raddr does. Changes are written to the keyfs. In passwords,
  \s stands for a space, since table cells lose trailing spaces.

  Background:
    Given a keyfs with the users
      | user   | password        |
      | fog    | fog-password    |
      | glenda | glenda-password |
    And an auth server on that keyfs

  @FOG_AUTHPASS_001
  Scenario: A user changes their password
    Given "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    When the client asks to change the password from "glenda-password" to "new-password-1"
    Then the server replies AuthOK
    And the keyfs holds the DES and AES keys of "new-password-1" for "glenda"
    And "glenda" can get tickets with password "new-password-1" and not with "glenda-password"

  @FOG_AUTHPASS_001
  Scenario: The AuthTp ticket names the user and carries the request's challenge
    Given "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    Then the ticket is a form 1 AuthTp ticket for client user "glenda" and server user "glenda" with the request's challenge

  @FOG_AUTHPASS_002
  Scenario: A user changes only their secret
    Given "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    When the client asks to keep the password "glenda-password" and set the secret "pop-secret"
    Then the server replies AuthOK
    And the keyfs secret of "glenda" reads "pop-secret"
    And the keyfs holds the DES and AES keys of "glenda-password" for "glenda"

  @FOG_AUTHPASS_003
  Scenario Outline: A refused request can be retried on the same connection
    Given "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    When the client asks to change the password from "<old>" to "<new>"
    Then the server replies AuthErr "<error> 127.0.0.1"
    And the keyfs holds the DES and AES keys of "glenda-password" for "glenda"
    When the client asks to change the password from "glenda-password" to "new-password-1"
    Then the server replies AuthOK
    And the keyfs holds the DES and AES keys of "new-password-1" for "glenda"

    Examples:
      | old             | new             | error                             |
      | wrong-password  | new-password-1  | protocol botch2:                  |
      | glenda-password | short           | password must be at least 8 chars |
      | glenda-password | abcdefg\s\s\s   | password must be at least 8 chars |
      | glenda-password | anonymous       | trivial password                  |
      | glenda-password | suomynona       | trivial password                  |
      | glenda-password | change me       | trivial password                  |
      | glenda-password | no passwd       | trivial password                  |
      | glenda-password | dwssap on       | trivial password                  |
      | glenda-password | em egnahc       | trivial password                  |
      | glenda-password | login           | password must be at least 8 chars |
      | glenda-password | guest           | password must be at least 8 chars |
      | glenda-password | passwd          | password must be at least 8 chars |

  @FOG_AUTHPASS_003
  Scenario: An eight-character password is long enough
    Given "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    When the client asks to change the password from "glenda-password" to "8chars!x"
    Then the server replies AuthOK
    And the keyfs holds the DES and AES keys of "8chars!x" for "glenda"

  @FOG_AUTHPASS_011
  Scenario: A new password whose AES key holds a zero byte is stored whole
    Given "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    When the client changes the password to one whose AES key holds a zero byte
    Then the server replies AuthOK
    And the keyfs holds the DES and AES keys of that password for "glenda"

  @FOG_AUTHPASS_004
  Scenario: An AES key holding a zero byte is still checked against the old password
    Given the keyfs user "glenda" has the DES key of "des-password" and the AES key of a password whose AES key holds a zero byte
    And "glenda" has an AuthTp ticket after a PAK exchange with that password
    When the client asks to change the password from "des-password" to "new-password-1"
    Then the server replies AuthErr "protocol botch3: 127.0.0.1"

  @FOG_AUTHPASS_004
  Scenario: A DES key from the old password with an AES key from another is refused
    Given the keyfs user "glenda" has the DES key of "des-password" and the AES key of "glenda-password"
    And "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    When the client asks to change the password from "des-password" to "new-password-1"
    Then the server replies AuthErr "protocol botch3: 127.0.0.1"

  @FOG_AUTHPASS_005
  Scenario: A password request that does not open under the ticket ends the connection
    Given "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    When the client sends a password request sealed with another key
    Then the server replies AuthErr "protocol botch1: 127.0.0.1"
    And the server closes the connection

  @FOG_AUTHPASS_006
  Scenario Outline: AuthPass needs the PAK key of an exchange for the uid alone, as authsrv's ukey
    When a client sends an AuthPass request for "glenda" <after>
    Then the server replies AuthErr "DES is disabled"
    And the server closes the connection

    Examples:
      | after                                            |
      | without a PAK exchange                           |
      | after a PAK exchange as host "glenda" for "fog"  |

  @FOG_AUTHPASS_007
  Scenario Outline: A user who cannot authenticate gets an AuthTp ticket nobody can open
    Given the keyfs user "glenda" is <state>
    When "glenda" asks for an AuthTp ticket after a PAK exchange with password "glenda-password"
    Then the server replies AuthOK and a form 1 ticket that does not open with that PAK key

    Examples:
      | state                              |
      | unknown                            |
      | disabled                           |
      | expired                            |
      | in purgatory after 10 bad attempts |

  @FOG_AUTHPASS_008
  Scenario Outline: A change the keyfs cannot save is refused and can be retried
    Given "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    And saving the keyfs database fails
    When <request>
    Then the server replies AuthErr "<error> 127.0.0.1"
    And the keyfs holds the DES and AES keys of "glenda-password" for "glenda"

    Examples:
      | request                                                                    | error              |
      | the client asks to change the password from "glenda-password" to "new-password-1" | can't write key    |
      | the client asks to keep the password "glenda-password" and set the secret "pop-secret" | can't write secret |

  @FOG_AUTHPASS_009
  Scenario: A successful change clears the user's bad attempts
    Given the keyfs log of "glenda" reads "3"
    And "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    When the client asks to change the password from "glenda-password" to "new-password-1"
    Then the server replies AuthOK
    And the keyfs log of "glenda" reads "0"

  @FOG_AUTHPASS_010
  Scenario: The PAK key serves one AuthPass request
    Given "glenda" has an AuthTp ticket after a PAK exchange with password "glenda-password"
    And the client asked to change the password from "glenda-password" to "new-password-1" and got AuthOK
    When the client sends an AuthPass request for "glenda" without a PAK exchange
    Then the server replies AuthErr "DES is disabled"
