@fog_foundation @namespace_authorization @design
Feature: An authorization layer bounds every resource operation in a principal's view
  NinePSharp.Namespaces.Authorization is a separate layer over the provider interface
  that the namespace data plane already uses. It does not authenticate anyone: a host
  supplies a trusted principal and an immutable policy. Native permission rules follow
  the 9front sources: gefs fsaccess, ingroup and mode2bits (sys/src/cmd/gefs/fs.c) for
  mode checks, group membership, open bits, walk, create masking and remove; hjfs
  (sys/src/cmd/hjfs/fs2.c) for the remove-on-close parent check, which gefs omits but
  open(5) requires. Job scope (FOG_N07) is out of scope for this layer's first slice.
  These scenarios require executable bindings before they count as implemented.

  @NS_AUTHZ_001 @FOG_N02
  Scenario Outline: Every authorization layer must allow an operation
    Given a principal whose policy allows reading a file in every layer
    When only the <layer> layer denies the read
    Then the open is denied before provider dispatch

    Examples:
      | layer                               |
      | enabled principal                   |
      | current policy generation           |
      | explicit user or group grant        |
      | owner, group and other mode bits    |
      | mount restriction                   |

  @NS_AUTHZ_002
  Scenario Outline: Mode bits are consulted as gefs fsaccess does
    Given a file whose owner, group and other permission sets differ
    When <caller> opens it
    Then the <sets> permission sets are tried in order, and one set must grant every requested bit

    Examples:
      | caller                                   | sets                   |
      | the file's owner, not in its group       | owner and other        |
      | the file's owner, also in its group      | owner, group and other |
      | a member of the file's group             | group and other        |
      | a principal neither owner nor member     | other                  |
      | the user none                            | other                  |

  @NS_AUTHZ_002
  Scenario: Members of nogroup lose the other set except to search directories
    Given a principal in group nogroup who is neither owner nor member of a file's group
    When it opens a file that only the other set permits
    Then the open is denied
    And searching a directory that only the other set permits is still allowed

  @NS_AUTHZ_003 @FOG_N04
  Scenario: Group membership is the same-name group plus explicit members, not recursive
    Given a group grant and a policy listing that group's direct members
    When a principal whose username equals the group name requests access
    Then it is a member, as gefs and hjfs ingroup treat uid equal to gid
    And membership of a group that is itself listed as a member is not inherited

  @NS_AUTHZ_004
  Scenario: Walking requires the walk right and search permission on every directory
    Given a path that crosses a directory, a mount point and a union
    When the principal walks it
    Then each directory left requires the walk grant right and execute mode permission
    And the walk stops at the first directory that fails either check

  @NS_AUTHZ_005
  Scenario: A hidden object is indistinguishable from an absent one
    Given an object on which the principal holds no grant right
    When the principal walks to it, lists its directory or stats it by a known name
    Then the walk reports not found, the listing omits it and stat reports not found
    And no reply distinguishes it from a name that does not exist

  @NS_AUTHZ_006 @FOG_N02
  Scenario: An unauthorized union member cannot be reached as a later member
    Given a union whose first member hides a name that a later member grants
    When the principal walks or lists the union
    Then lookup skips the hidden member as if it lacked the name
    And a union read skips a member whose open is definitively rejected

  @NS_AUTHZ_007
  Scenario Outline: Each open mode requires its grant rights and mode permission
    Given a principal opening a regular file
    When it requests <mode>
    Then it needs the <rights> grant rights and <permission>

    Examples:
      | mode    | rights        | permission                          |
      | OREAD   | read          | read mode permission                |
      | OWRITE  | write         | write mode permission               |
      | ORDWR   | read, write   | read and write mode permission      |
      | OEXEC   | read          | read and execute mode permission    |
      | OTRUNC  | write         | write mode permission               |
      | ORCLOSE | remove        | write mode permission in its parent |

  @NS_AUTHZ_008
  Scenario: A handle carries only the rights admitted at its open
    Given a handle opened for reading through the layer
    When the principal writes through it
    Then the write is denied before provider dispatch
    And a handle that was never opened through the layer is rejected

  @NS_AUTHZ_009 @FOG_N05
  Scenario: Ordinary mode changes preserve an open handle but deny new opens
    Given a handle opened for reading under valid authority
    When the file's mode stops granting read permission
    Then the existing handle can still read
    And a new read open is denied

  @NS_AUTHZ_010 @FOG_N05
  Scenario: Policy generation revocation invalidates retained handles
    Given a handle opened under policy generation one
    When generation two becomes current
    Then reads and writes through the handle are denied
    And clunk still releases the provider's open state

  @NS_AUTHZ_011
  Scenario: Create checks the parent and masks the child's permissions
    Given a directory where the principal holds the create grant right and write permission
    When it creates a file and a directory with generous permissions
    Then the file receives perm & (~0666 | dir.perm & 0666)
    And the directory receives perm & (~0777 | dir.perm & 0777)
    And the open mode must be covered by tree grants the new child inherits
    And a denied create is a definite rejection with no provider call

  @NS_AUTHZ_012
  Scenario: Remove needs the remove right and write permission in its parent
    Given a file whose parent is reported by its provider
    When the principal removes it
    Then it needs the remove grant right on the file and write permission on the parent
    And a denied remove reaches no provider

  @NS_AUTHZ_013 @FOG_N03
  Scenario: A read-only mount attenuates a writable resource
    Given an otherwise writable resource reached through a read-only mount
    When write, truncate, create, remove, remove-on-close and ctl writes are requested
    Then every mutation is denied before resource dispatch
    And permitted reads return the same stable resource identity

  @NS_AUTHZ_014
  Scenario: Tree grants need containment attested by the provider
    Given a tree grant on a directory
    When the layer checks a descendant
    Then containment comes only from the provider's parent relation
    And a provider without that relation cannot be given tree grants
    And a parent cycle or a chain beyond the depth bound is denied

  @NS_AUTHZ_015 @FOG_N06
  Scenario: Moving an open object does not authorize its new neighbours
    Given a read handle admitted under a tree grant
    When the object moves outside that tree
    Then the handle still reads only its original object
    And new walks and opens of the new parent or siblings use the current tree

  @NS_AUTHZ_016 @FOG_N02
  Scenario Outline: A claimed identity is not authority
    Given a principal without write authority to a protected file
    When it presents <claim>
    Then the layer evaluates only its own principal and policy and denies the write

    Examples:
      | claim                                            |
      | a resource handle it never walked to             |
      | an operation context naming another user         |
      | an alias walk to the same stable resource        |

  @NS_AUTHZ_017
  Scenario: Stat needs the stat right but no mode permission
    Given a visible file with no mode permissions for the principal
    When it stats the file with and without the stat grant right
    Then stat succeeds only with the stat right, as stat(5) needs no mode permission

  @NS_AUTHZ_018
  Scenario: Reading a directory needs the read right and read permission
    Given a directory the principal may walk but not read
    When it opens the directory for reading
    Then the open is a definite directory rejection

  @NS_AUTHZ_019
  Scenario: Metadata updates are unavailable through the first slice
    Given a provider that supports wstat
    When a principal requests wstat through the layer
    Then the layer does not expose the provider's wstat capability

  @NS_AUTHZ_020
  Scenario: An invalid policy is rejected when the view is built
    Given a policy with a zero generation, an empty rights set, a duplicate grant or an unknown group member
    When a view is built from it
    Then construction fails and no view exists
