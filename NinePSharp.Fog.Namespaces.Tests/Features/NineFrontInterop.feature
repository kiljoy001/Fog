@fog_interop
Feature: A stock 9front terminal logs in to Fog and mounts its namespace
  9front runs unchanged in QEMU from its release ISO, with only a serial console set in plan9.ini.
  Its ndb names Fog's auth server for the auth domain, its factotum holds glenda's dp9ik key, and
  srv(4) mounts Fog's plain user listener, authenticating on the afid through factotum and Fog's
  auth server as a 9front file server would. Scenarios are skipped unless FOG_9FRONT_ISO names a
  9front amd64 ISO and qemu-system-x86_64 is on the path.

  Background:
    Given a Fog host for the auth domain "fog.example" as "fog" with keyfs users
      | user   | password        |
      | fog    | fog-password    |
      | glenda | glenda-password |
    And the shared root mounts "mail" with "inbox" reading "hello mail" and "notes" with "todo" reading "remember"
    And "glenda" may read and write "/mnt/mail"
    And a 9front terminal in Fog's auth domain

  @FOG_INTEROP_001
  Scenario: srv authenticates with dp9ik and mounts Fog's namespace as the user
    Given the terminal's factotum holds the dp9ik key of "glenda" with password "glenda-password"
    When the terminal runs srv for Fog's user listener on /n/fog
    And the terminal runs "cat /n/fog/mnt/mail/inbox"
    Then the terminal prints "hello mail"
    And the export attached "glenda"

  @FOG_INTEROP_002
  Scenario: The mounted namespace shows only what the user's grants allow
    Given the terminal's factotum holds the dp9ik key of "glenda" with password "glenda-password"
    When the terminal runs srv for Fog's user listener on /n/fog
    And the terminal runs "ls /n/fog/mnt"
    Then the terminal prints "/n/fog/mnt/mail"
    And the terminal does not print "notes"

  @FOG_INTEROP_003
  Scenario: A write through the mount reaches the application
    Given the terminal's factotum holds the dp9ik key of "glenda" with password "glenda-password"
    When the terminal runs srv for Fog's user listener on /n/fog
    And the terminal runs "echo -n new mail >/n/fog/mnt/mail/inbox"
    And the terminal runs "cat /n/fog/mnt/mail/inbox"
    Then the terminal prints "new mail"
    And the mail application recorded one write

  @FOG_INTEROP_004
  Scenario: A wrong password is refused, and factotum asks for the key again
    Given the terminal's factotum holds the dp9ik key of "glenda" with password "wrong-password"
    When the terminal starts srv for Fog's user listener on /n/fog
    Then factotum asks for the dp9ik key of "fog.example"
    And the export attached nobody
    When the terminal answers factotum as "glenda" with password "glenda-password"
    And the terminal runs "cat /n/fog/mnt/mail/inbox"
    Then the terminal prints "hello mail"
    And the export attached "glenda"

  @FOG_INTEROP_005
  Scenario: passwd(1) changes the password through Fog's auth server
    When the terminal changes the password of "glenda" in "fog.example" from "glenda-password" to "glenda-password-2"
    Then the terminal's passwd exits without an error
    And the keyfs holds the keys of "glenda-password-2" for "glenda"
