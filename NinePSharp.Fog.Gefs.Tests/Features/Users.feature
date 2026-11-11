@fog_gefs
Feature: The user table is parsed from /adm/users as gefs's user.c parses it
  Each line of /adm/users is id:name:leader:members, # starting a comment, and is read only up to
  its newline. A user is in a group that is itself, by its id, or that names it a member, each
  user found by the first line of its name and each member by the last. A table with a line
  that is short of a field, or that names a leader or member who is not a user, is refused with
  gefs's error, the line's fault written to the console. Fog's files name their users rather
  than numbering them, so a user and a group are found by name.

  @FOG_GEFS_901
  Scenario: The table gefs reams names the user who reams it adm's member and its own group's leader
    When the table is reamed for "glenda"
    Then the reamed table is "-1:adm::glenda\n0:none::\n1:glenda:glenda:\n"

  @FOG_GEFS_902
  Scenario Outline: A user is in a group that is itself or that names it
    Given the user table "-1:adm::glenda\n0:none::\n# the users\n\n1:glenda:glenda:\n2:sys::,glenda,,ken:\n3:ken::\n"
    Then "<user>" <is> in the group "<group>"

    Examples:
      | user   | is     | group  |
      | glenda | is     | sys    |
      | ken    | is     | sys    |
      | none   | is not | sys    |
      | glenda | is     | glenda |
      | ken    | is not | glenda |
      | glenda | is     | adm    |
      | glenda | is not | nobody |
      | nobody | is not | glenda |
      | nobody | is not | nobody |

  @FOG_GEFS_902
  Scenario Outline: A user and a group of the same id are one, ids read as atol reads them
    Given the user table "<table>"
    Then "a" <is> in the group "b"

    Examples:
      | table                 | is     |
      | 5:a::\n5:b::\n        | is     |
      | +7:a::\n7:b::\n       | is     |
      | \t 8:a::\n8:b::\n     | is     |
      | x9:a::\n0:b::\n       | is     |
      | -3:a::\n3:b::\n       | is not |
      | 12:a::\n1:b::\n       | is not |
      | 1:a::\n12:b::\n       | is not |
      | 2:a::\n12:b::\n       | is not |

  @FOG_GEFS_902
  Scenario: A user is found by the first line of its name, and a member by the last
    Given the user table "1:m::\n2:m::\n3:g::m\n4:u::\n5:x::u\n6:x::\n"
    Then "m" is not in the group "g"
    And "u" is in the group "x"

  @FOG_GEFS_902
  Scenario: A last line without its newline is not read
    Given the user table "1:glenda::\n2:sys::glenda"
    Then "glenda" is not in the group "sys"

  @FOG_GEFS_902
  Scenario: The members end at the next colon
    Given the user table "1:glenda::\n2:sys::glenda:ken\n"
    Then "glenda" is in the group "sys"
    And "ken" is not in the group "sys"

  @FOG_GEFS_903
  Scenario Outline: A table with a faulty line is refused, the fault written to the console
    When the user table "<table>" is parsed
    Then the parse fails with "<error>"
    And the console says "<said>"

    Examples:
      | table                           | error               | said                                          |
      | 1:glenda::\n2\n                 | syntax error        | /adm/users:2: missing ':' after name\n        |
      | 1:glenda::\n# no\n2:sys\n       | syntax error        | /adm/users:3: missing ':' after name\n        |
      | 1:glenda::\n2:sys:\n            | syntax error        |                                               |
      | 1:glenda::\n2:sys:bob:\n        | user does not exist | /adm/users:2: leader bob does not exist\n     |
      | 1:glenda:bob:\n2:sys\n          | user does not exist | /adm/users:1: leader bob does not exist\n     |
      | 1:glenda::\n2:sys::glenda,bob\n | user does not exist | /adm/users:2: user bob does not exist\n       |

  @FOG_GEFS_903
  Scenario: A leader who is a user is taken
    When the user table "1:glenda::\n2:sys:glenda:\n" is parsed
    Then the parse succeeds
