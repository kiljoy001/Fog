@fog_foundation @applications @design
Feature: Installing an application is dropping its module into /bin
  An application is a long-lived grain. Its WASM module lives at /bin/{app}; while it is
  available, its file tree is mounted at /mnt/{app}. Installation has no step beyond writing
  the module, as on Plan 9 where a program is installed by copying it into /bin. Admission
  rules for the module are in fog-v1-profiles/Wasm.md. This is a later slice than
  NamespaceViews.feature. These scenarios require executable bindings before they count as
  implemented.

  @FOG_APP_001
  Scenario: Writing a module into /bin installs the application
    Given a principal granted create and write on /bin
    When it writes a valid module to /bin/mail and closes it
    Then /mnt/mail appears in the shared root
    And no other installation step exists

  @FOG_APP_002
  Scenario: Installing needs authority over /bin
    Given a principal without the create right on /bin
    When it writes /bin/mail
    Then the create is denied and /mnt/mail does not appear

  @FOG_APP_003
  Scenario: A module that fails admission is not mounted
    Given a module that the WASM profile rejects
    When it is written to /bin/broken and closed
    Then /mnt/broken does not appear
    And the rejection reason is readable by the principal that wrote it

  @FOG_APP_004
  Scenario: An application activates on use and keeps its name when idle
    Given an installed application with no active grain
    When a principal walks into /mnt/mail
    Then the application's grain activates
    And after it deactivates when idle, /mnt/mail still exists

  @FOG_APP_005
  Scenario: Replacing a module keeps open files on the old version
    Given open fids below /mnt/mail
    When /bin/mail is replaced with a new valid module
    Then new opens use the new module
    And the open fids keep the version they opened

  @FOG_APP_006
  Scenario: Removing the module removes the application
    Given an installed application
    When /bin/mail is removed
    Then /mnt/mail disappears once its open fids are clunked

  @FOG_APP_007
  Scenario: An application sees only its own namespace
    Given an installed application
    When its module runs
    Then it has its own process-group grain
    And it reaches only what that namespace mounts, with no ambient file, socket or credential authority
