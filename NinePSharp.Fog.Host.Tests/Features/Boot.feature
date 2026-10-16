@fog_host_boot
Feature: The host boots the kernel on a gefs store kept in a file
  Fog's host keeps its root on a gefs store in a file on its own disk. When the file does not exist
  the host makes it, reamed at the size its settings give and owned by the host owner, as gefs -r
  does; when it exists the host opens it, as gefs does at boot. A file that is not a store is
  reported and left as it is: the host never reams a file that exists. The kernel then boots on
  the store as bootrc and /lib/namespace build the root. Until Fog has a mount driver the store is
  served in the host's own process, and its files stat as the mount driver's, device M. What the store holds is committed while
  the host runs and when it stops, so it is there when the host boots again.

  Background:
    Given a host whose store file is in a new directory

  @FOG_HOST_001
  Scenario: The host makes the store when its file does not exist
    When the host boots
    Then the store file exists
    And the names in "/root" are "adm bin lib rc tmp usr"
    And "/root" belongs to the host owner
    And "/root" is a directory of device "M"

  @FOG_HOST_002
  Scenario: What was written is read after the host restarts
    Given the host has booted
    When the process creates "/tmp/notes" holding "kept"
    And the host restarts
    Then reading "/tmp/notes" gives "kept"

  @FOG_HOST_002
  Scenario: An application copied into /bin is there after the host restarts
    Given the host has booted
    When the process creates "/bin/app" holding "an application"
    And the host restarts
    Then reading "/bin/app" gives "an application"

  @FOG_HOST_003
  Scenario: A file that is not a store is reported and left as it is
    Given the store file holds "not a store"
    When the host boots
    Then the boot fails with "device size is not a whole number of blocks"
    And the store file holds "not a store"

  @FOG_HOST_003
  Scenario: A boot that fails after the store is opened lets the store file go
    Given the host is to install a program named "a/b"
    When the host boots
    Then the boot fails with "create/wstat -- bad character in file name"
    When the host is to install no programs
    And the host boots
    Then the names in "/root" are "adm bin lib rc tmp usr"

  @FOG_HOST_004
  Scenario: Until the store's users are read, each user is in only the group of its own name
    Given the host has booted
    And the process has created "/tmp/notes" holding "kept"
    When the process changes the group of "/tmp/notes" to "sys"
    Then the change fails with "wstat -- not in group"
    When the process changes the group of "/tmp/notes" to "glenda"
    Then "/tmp/notes" has the group "glenda"
