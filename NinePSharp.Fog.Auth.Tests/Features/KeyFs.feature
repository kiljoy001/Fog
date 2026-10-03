@fog_keyfs
Feature: Fog keeps its authentication database as 9front keyfs does
  The host is the auth server for its authdom. Its user database is served as the
  keyfs(4) two-level tree, /mnt/keys/{user}/{key,aeskey,pakhash,secret,log,status,
  expire,warnings}, with the semantics of 9front sys/src/cmd/auth/keyfs.c, so
  auth/changeuser and authsrv use it unchanged. The database file is
  sealed by a storage key held by the host's TPM, as keyfs's file is by the key in
  nvram. The goal is protecting the keys at rest: the seal has no PCR policy and makes
  no claim against a compromised host. Keys are compared with the Dp9ik package, whose
  passtokey and authpak_hash are verified against 9front in dp9ik.net. That the tree is
  absent from node namespace exports is FOG_VIEW_012 in NamespaceViews.feature.

  Background:
    Given a keyfs whose storage key is sealed by a software TPM
    And the user "glenda" with password "glenda-password"

  # --- The tree --------------------------------------------------------------

  @FOG_KEYFS_001
  Scenario: Each user is a directory of the keyfs files
    When the keyfs root is listed
    Then it contains exactly the directory "glenda"
    When "glenda" is listed
    Then it contains exactly "key, aeskey, pakhash, secret, log, status, expire, warnings"
    And every entry is owned by "auth", directories with mode 0777 and files with mode 0666

  @FOG_KEYFS_002
  Scenario: Keys are those passtokey and authpak_hash derive
    When "glenda/key", "glenda/aeskey" and "glenda/pakhash" are read
    Then they equal the Dp9ik passtokey DES and AES keys and authpak_hash for "glenda" and "glenda-password"

  @FOG_KEYFS_003
  Scenario: Writing an AES key recomputes the PAK hash
    When the AES key of "new-password" is written to "glenda/aeskey"
    Then "glenda/pakhash" equals the Dp9ik authpak_hash for "glenda" and "new-password"

  @FOG_KEYFS_004
  Scenario Outline: Key and secret writes must have their exact sizes
    When <bytes> bytes are written to "glenda/<file>"
    Then the write fails with "garbled write data"

    Examples:
      | file   | bytes |
      | key    | 6     |
      | key    | 8     |
      | aeskey | 15    |
      | aeskey | 17    |
      | secret | 32    |

  # --- Users -----------------------------------------------------------------

  @FOG_KEYFS_005
  Scenario: Making a directory creates an enabled user that never expires
    When the directory "scott" is made in the keyfs root
    Then "scott/status" reads "ok"
    And "scott/expire" reads "never"
    And "scott/log" reads "0"

  @FOG_KEYFS_006
  Scenario Outline: User names follow keyfs's rules
    When the directory "<name>" is made in the keyfs root
    Then making it fails with "<error>"

    Examples:
      | name                         | error               |
      |                              | empty file name     |
      | abcdefghijklmnopqrstuvwxyz01 | file name too long  |
      | glenda                       | user already exists |
      | has space                    | bad user name       |
      | has\tcontrol                 | bad user name       |

  @FOG_KEYFS_007
  Scenario: A 27-byte user name is the longest accepted
    When the directory "abcdefghijklmnopqrstuvwxyz0" is made in the keyfs root
    Then the keyfs root contains the directory "abcdefghijklmnopqrstuvwxyz0"

  @FOG_KEYFS_008
  Scenario: Renaming a user directory renames the user and keeps its keys
    When "glenda" is renamed to "glenda2"
    Then the keyfs root contains exactly the directory "glenda2"
    And "glenda2/aeskey" equals the AES key of "glenda-password"
    And "glenda2/pakhash" equals the Dp9ik authpak_hash for "glenda2" and "glenda-password"

  @FOG_KEYFS_009
  Scenario: Removing a user directory removes the user
    When "glenda" is removed
    Then the keyfs root is empty
    And reading "glenda/key" through a fid walked before the removal still fails

  @FOG_KEYFS_010
  Scenario: Only user directories can be made, removed or renamed
    Then making the file "plain" in the keyfs root fails with "permission denied"
    And making anything inside "glenda" fails with "permission denied"
    And removing "glenda/key" fails with "permission denied"
    And removing the keyfs root fails with "permission denied"
    And writing to "glenda" fails with "permission denied"
    And writing to the keyfs root fails with "permission denied"

  @FOG_KEYFS_043
  Scenario Outline: A fid walked to a removed user no longer works
    Given a fid walked to "glenda/<file>"
    When "glenda" is removed through another fid
    Then <request> through the walked fid fails with "<error>"

    Examples:
      | file | request | error                   |
      | key  | opening | user removed            |
      | key  | reading | user removed            |
      | key  | writing | user removed            |
      | key  | stat    | user removed            |
      | key  | removing | user removed           |
      |      | wstat   | user previously removed |

  # --- Account state ---------------------------------------------------------

  @FOG_KEYFS_011
  Scenario Outline: A disabled, locked-out or expired user's keys cannot be read
    Given "glenda" is <state>
    Then reading each of "key, aeskey, pakhash, secret" in "glenda" fails with "<error>"
    And "glenda/status" reads "<status>"

    Examples:
      | state                               | error             | status   |
      | disabled                            | user disabled     | disabled |
      | in purgatory after 10 bad attempts  | user in purgatory | ok       |
      | expired                             | user expired      | expired  |

  @FOG_KEYFS_012
  Scenario: A disabled user is reported before purgatory and expiry
    Given "glenda" is disabled, in purgatory and expired
    Then reading "glenda/key" fails with "user disabled"

  @FOG_KEYFS_013
  Scenario: Every tenth consecutive bad attempt locks the user out for that many seconds
    When "bad" is written to "glenda/log" 9 times
    Then "glenda/key" can be read
    When "bad" is written to "glenda/log" once more
    Then reading "glenda/key" fails with "user in purgatory" until 10 seconds have passed
    When "bad" is written to "glenda/log" 10 more times after that
    Then reading "glenda/key" fails with "user in purgatory" until 20 seconds have passed

  @FOG_KEYFS_014
  Scenario Outline: Good attempts and status changes clear the bad-attempt count
    Given "glenda/log" reads "7"
    When "<value>" is written to "glenda/<file>"
    Then "glenda/log" reads "0"

    Examples:
      | file   | value |
      | log    | good  |
      | status | ok    |

  @FOG_KEYFS_015
  Scenario Outline: Status writes take their first line, as keyfs does
    Given "glenda/status" reads "<before>"
    When "<value>" is written to "glenda/status"
    Then the write succeeds and "glenda/status" reads "<after>"

    Examples:
      | before   | value       | after    |
      | ok       | disabled    | disabled |
      | ok       | disabled\n  | disabled |
      | disabled | ok          | ok       |
      | disabled | ok\nignored | ok       |

  @FOG_KEYFS_015
  Scenario Outline: Expiry writes accept "never" or seconds since the epoch
    Given "glenda/expire" reads "<before>"
    When "<value>" is written to "glenda/expire"
    Then the write succeeds and "glenda/expire" reads "<after>"

    Examples:
      | before     | value        | after      |
      | never      | 4102444800   | 4102444800 |
      | never      | 4102444800\n | 4102444800 |
      | 4102444800 | never        | never      |
      | never      | 4294967295   | 4294967295 |

  @FOG_KEYFS_015
  Scenario Outline: Malformed writes are refused and change nothing
    When "<value>" is written to "glenda/<file>"
    Then the write fails with "<error>"
    And "glenda/status" reads "ok"
    And "glenda/expire" reads "never"

    Examples:
      | file    | value      | error               |
      | status  | enabled    | unknown status      |
      | status  | \nok       | unknown status      |
      | expire  | tomorrow   | bad expiration date |
      | expire  | 12x        | bad expiration date |
      | expire  |            | bad expiration date |
      | expire  | -1         | bad expiration date |
      | expire  | 4294967296 | bad expiration date |
      | pakhash | anything   | permission denied   |

  @FOG_KEYFS_015
  Scenario Outline: Warning counts are read as strtoul reads them into a byte
    When "<value>" is written to "glenda/warnings"
    Then the write succeeds and "glenda/warnings" reads "<count>"

    Examples:
      | value | count |
      | 7     | 7     |
      | 3x    | 3     |
      | x     | 0     |
      | 300   | 44    |

  @FOG_KEYFS_015
  Scenario: Only the exact word "good" clears the bad-attempt count, as in keyfs
    Given "glenda/log" reads "2"
    When "good\n" is written to "glenda/log"
    Then "glenda/log" reads "3"

  @FOG_KEYFS_042
  Scenario Outline: An account expires only after its expiry second
    Given "glenda" expires <when>
    Then <outcome>
    And "glenda/status" reads "<status>"

    Examples:
      | when           | outcome                                        | status  |
      | now            | "glenda/key" can be read                       | ok      |
      | one second ago | reading "glenda/key" fails with "user expired" | expired |

  @FOG_KEYFS_033
  Scenario: Text files end with a newline and a new user's secret is empty
    When the directory "scott" is made in the keyfs root
    Then the bytes of "scott/status" are "ok\n"
    And the bytes of "scott/expire" are "never\n"
    And the bytes of "scott/log" are "0\n"
    And the bytes of "scott/warnings" are "0\n"
    And the bytes of "scott/secret" are ""

  @FOG_KEYFS_016
  Scenario: Setting the expiry resets the warning count
    Given "glenda/warnings" reads "2"
    When "4102444800" is written to "glenda/expire"
    Then "glenda/warnings" reads "0"

  # --- Persistence and the sealed storage key --------------------------------

  @FOG_KEYFS_017
  Scenario: Every change is written to the database before the request succeeds
    When the directory "scott" is made, "scott/aeskey" is written and "glenda/status" is set to "disabled"
    And the keyfs is restarted on the same TPM
    Then "scott/aeskey" holds the written key
    And "glenda/status" reads "disabled"

  @FOG_KEYFS_032
  Scenario: Every stored field survives a restart
    When "glenda/key" is set to the DES key of "other-password"
    And "0123456789abcdef0123456789abcde" is written to "glenda/secret"
    And "4102444800" is written to "glenda/expire"
    And "3" is written to "glenda/warnings"
    And the keyfs is restarted on the same TPM
    Then "glenda/key" equals the DES key of "other-password"
    And "glenda/aeskey" equals the AES key of "glenda-password"
    And "glenda/pakhash" equals the Dp9ik authpak_hash for "glenda" and "glenda-password"
    And "glenda/secret" reads "0123456789abcdef0123456789abcde"
    And "glenda/expire" reads "4102444800"
    And "glenda/warnings" reads "3"

  @FOG_KEYFS_037
  Scenario: A new keyfs reopens its database before any change
    When another keyfs is initialised on another empty software TPM and restarted at once
    Then its keyfs root is empty

  @FOG_KEYFS_037
  Scenario: A database with no users is saved and reopened
    When "glenda" is removed
    And the keyfs is restarted on the same TPM
    Then the keyfs root is empty

  @FOG_KEYFS_018
  Scenario: The bad-attempt count is not persisted, as in keyfs
    Given "glenda/log" reads "3"
    When the keyfs is restarted on the same TPM
    Then "glenda/log" reads "0"

  @FOG_KEYFS_019
  Scenario: The database file reveals no user names or keys
    When the database file is read from disk
    Then it does not contain the bytes of "glenda", its DES key or its AES key

  @FOG_KEYFS_035
  Scenario: Every save encrypts with a fresh nonce
    When "glenda/warnings" is written 20 times, keeping each database file
    Then the 20 database files have 20 different nonces

  @FOG_KEYFS_034
  Scenario: Every new database has its own random storage key
    When another keyfs is initialised on another empty software TPM
    Then the two recovery phrases differ

  @FOG_KEYFS_036
  Scenario: The state directory and its files are the service account's alone
    Then the state directory has mode 0700
    And the database file and the sealed key file have mode 0600

  @FOG_KEYFS_036
  Scenario: A state directory others could read is made the service account's alone
    Given the state directory has mode 0755
    When the keyfs is restarted on the same TPM
    Then the state directory has mode 0700

  @FOG_KEYFS_036
  Scenario: A keyfs creates a state directory that does not exist yet
    When a keyfs is initialised in a state directory that does not exist yet
    Then that state directory exists with mode 0700

  @FOG_KEYFS_038
  Scenario Outline: A truncated database file is refused
    When the database file is cut to <length> bytes
    And the keyfs is restarted on the same TPM
    Then the keyfs refuses to start with "keyfs: database authentication failed"

    Examples:
      | length |
      | 0      |
      | 8      |
      | 20     |
      | 35     |

  @FOG_KEYFS_020
  Scenario: A modified database file is refused rather than misread
    When any single byte of the database file is changed
    And the keyfs is restarted on the same TPM
    Then the keyfs refuses to start with "keyfs: database authentication failed"

  @FOG_KEYFS_021
  Scenario: The database cannot be opened with another TPM
    When the database file and sealed key are moved to a host with a different software TPM
    Then the keyfs refuses to start with "keyfs: cannot unseal storage key"

  @FOG_KEYFS_039
  Scenario Outline: A damaged sealed key file is refused
    When the sealed key file is <damage>
    And the keyfs is restarted on the same TPM
    Then the keyfs refuses to start with "keyfs: cannot unseal storage key"

    Examples:
      | damage                     |
      | emptied                    |
      | given another magic        |
      | extended by one byte       |
      | cut short                  |
      | given a garbled public area |

  @FOG_KEYFS_040
  Scenario: The sealed key cannot leave its TPM and needs no policy
    Then the sealed key's public area is fixedTPM and fixedParent with an empty authPolicy

  @FOG_KEYFS_041
  Scenario Outline: A state directory missing half its state does not start
    When the <file> file is deleted
    And the keyfs is restarted on the same TPM
    Then the keyfs refuses to start with "<error>"

    Examples:
      | file       | error                                                              |
      | sealed key | keyfs: no sealed storage key; recover the database with its phrase |
      | database   | keyfs: database missing                                            |

  @FOG_KEYFS_022
  Scenario: The seal holds no PCR policy
    When every PCR the software TPM lets locality 0 extend, 0 to 16 and 23, is extended
    And the keyfs is restarted on the same TPM
    Then "glenda/aeskey" equals the AES key of "glenda-password"

  @FOG_KEYFS_023
  Scenario: The keyfs holds no persistent TPM handles
    When the keyfs is started and restarted 50 times
    Then the software TPM has no persistent or transient handles

  @FOG_KEYFS_024
  Scenario Outline: A change whose save fails is refused and not applied
    Given "glenda/warnings" reads "2"
    When saving the database starts failing after part of the new file is written
    And <change>
    Then the change fails with "keyfs: database write failed"
    And "glenda" is unchanged
    When saving works again and the keyfs is restarted on the same TPM
    Then "glenda" is unchanged

    Examples:
      | change                                                   |
      | the directory "scott" is made in the keyfs root          |
      | the AES key of "new-password" is written to "glenda/aeskey" |
      | "disabled" is written to "glenda/status"                 |
      | "glenda" is renamed to "glenda2"                         |
      | "glenda" is removed                                      |
      | "glenda/warnings" is removed                             |

  # --- Recovery when the TPM is lost -----------------------------------------

  @FOG_KEYFS_025
  Scenario: A new database shows its recovery phrase once
    When a keyfs is initialised on an empty software TPM
    Then it reports a 24-word BIP-39 recovery phrase for its storage key
    And the phrase is not stored by the host

  @FOG_KEYFS_026
  Scenario: The recovery phrase reseals the database to a replacement TPM
    Given the recovery phrase of the database
    When the database file is moved to a host with a different software TPM
    And that host's state directory has mode 0755
    And the operator recovers the keyfs with the phrase
    Then "glenda/aeskey" equals the AES key of "glenda-password"
    And the state directory has mode 0700
    And the keyfs starts afterwards without the phrase

  @FOG_KEYFS_027
  Scenario Outline: A wrong recovery phrase is refused without changing anything
    Given the database file on a host with a different software TPM
    When the operator recovers the keyfs with <phrase>
    Then recovery fails with "<error>"
    And no storage key is sealed on that host

    Examples:
      | phrase                                     | error                                 |
      | a phrase with a wrong checksum word         | keyfs: invalid recovery phrase        |
      | a valid phrase for another database         | keyfs: database authentication failed |
      | a 12-word phrase                            | keyfs: invalid recovery phrase        |

  @FOG_KEYFS_044
  Scenario: Recovery needs the database file
    Given the recovery phrase of the database
    When the operator recovers the keyfs with the phrase on a host with no database file
    Then recovery fails with "keyfs: database missing"
    And no storage key is sealed on that host

  # --- Exposure: a local admin attach only -----------------------------------

  @FOG_KEYFS_028
  Scenario: The keyfs tree is served only on an owner-only local socket
    When the keyfs admin listener starts
    Then it listens on a Unix-domain socket and on no network address
    And the socket file is owned by the host's service account with mode 0600
    And a 9P client on the socket attaches and lists "glenda" in the keyfs root

  @FOG_KEYFS_029
  Scenario: A stale socket file does not stop the listener, and the socket is removed on shutdown
    Given a socket file left at the admin socket path by a process that was killed
    When the keyfs admin listener starts
    Then a 9P client on the socket attaches
    When the host shuts down
    Then the socket file no longer exists

  @FOG_KEYFS_030
  Scenario: The admin attach needs no 9P authentication, as keyfs does not
    When a 9P client on the admin socket sends Tauth
    Then it receives "keyfs: authentication not required"
    And an attach with afid NOFID succeeds


  @FOG_KEYFS_045
  Scenario Outline: The admin listener refuses a socket path it cannot own
    Given <obstacle>
    When the keyfs is restarted on the same TPM
    Then the keyfs refuses to start with "<error>" for the socket path
    And the failed start leaves no socket open

    Examples:
      | obstacle                                   | error                                |
      | a directory at the admin socket path       | keyfs: {path} is a directory          |
      | another keyfs serving the admin socket     | keyfs: another keyfs is serving {path} |
      | an admin socket path too long to bind      | keyfs: cannot listen on {path}        |

  @FOG_KEYFS_046
  Scenario: Shutting down closes connected admin clients
    Given a second 9P client attached on the admin socket
    When the host shuts down
    Then the second client's next request fails
    And the stopped keyfs holds no connections
