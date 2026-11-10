@fog_host_boot
Feature: The host boots the kernel on a gefs store kept in a file
  Fog's host keeps its root on a gefs store in a file on its own disk. When the file does not exist
  the host makes it, reamed at the size its settings give and owned by the host owner, as gefs -r
  does; when it exists the host opens it, as gefs does at boot. A file that is not a store is
  reported and left as it is: the host never reams a file that exists. The kernel then boots on
  the store as bootrc and /lib/namespace build the root. Until Fog has a mount driver the store is
  served in the host's own process, and its files stat as the mount driver's, device M. What the store holds is committed while
  the host runs and when it stops, so it is there when the host boots again. A store the host
  reams holds /adm/users as gefs reams it, with the group sys, whose members may write /bin. The
  users are read from it at each boot, as gefs loads them, and again when the console's users
  command asks; a table with a fault keeps the one there was, and one at boot stops the boot, as
  gefs does when it is not permissive. A store reamed before the host read its users has no
  /adm/users, and each user is then in only the group of its own name. At each boot the host
  writes the files its settings give, as 9front's installer copies them, and init then runs the
  boot's program with the console as its descriptors 0 to 2, keeping how it ended. Before the
  boot, the store is posted in /srv/boot and served over 9P there as gefs -s serves it, each attach
  authenticated through factotum, mounted from /srv/factotum where /mnt/factotum is not.

  Background:
    Given a host whose store file is in a new directory

  @FOG_HOST_001
  Scenario: The host makes the store when its file does not exist
    When the host boots
    Then the store file exists
    And the names in "/root" are "adm bin lib rc sys tmp usr"
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
    Given the host is to install a program named "a//b"
    When the host boots
    Then the boot fails with "create/wstat -- bad character in file name"
    When the host is to install no programs
    And the host boots
    Then the names in "/root" are "adm bin lib rc sys tmp usr"

  @FOG_HOST_004
  Scenario: A store the host reams holds gefs's user table, with sys, whose members may write /bin
    When the host boots
    Then reading "/root/adm/users" gives "-1:adm::glenda\n0:none::\n1:glenda:glenda:\n2:sys::glenda\n"
    And "/root/bin" has the group "sys" and the mode 775

  @FOG_HOST_004
  Scenario: The store's users are read from its /adm/users at each boot, as gefs loads them
    Given the host has booted
    And "/root/adm/users" holds "-1:adm::\n0:none::\n1:glenda:glenda:\n2:sys::glenda\n"
    And the process has created "/tmp/notes" holding "kept"
    When the host restarts
    And the process changes the group of "/tmp/notes" to "sys"
    Then "/tmp/notes" has the group "sys"
    When the process changes the group of "/tmp/notes" to "none"
    Then the change fails with "wstat -- not in group"

  @FOG_HOST_004
  Scenario: The users are read again when the console asks, as gefs's users command reads them
    Given the host has booted
    And "/root/adm/users" holds "-1:adm::\n0:none::\n1:glenda:glenda:\n2:sys::\n"
    And the process has created "/tmp/notes" holding "kept"
    When the console runs users
    Then the console prints "refreshed users\n"
    When the process changes the group of "/tmp/notes" to "sys"
    Then the change fails with "wstat -- not in group"

  @FOG_HOST_004
  Scenario Outline: A table the console cannot read leaves the one there was
    Given the host has booted
    And "/root/adm/users" holds "-1:adm::\n0:none::\n1:glenda:glenda:\n2:sys::glenda\n"
    And the console has run users
    And the process has created "/tmp/notes" holding "kept"
    And "/root/adm/users" holds <table>
    When the console runs users
    Then the console prints "<printed>"
    And the host's log says "<logged>"
    When the process changes the group of "/tmp/notes" to "sys"
    Then "/tmp/notes" has the group "sys"
    When the process changes the group of "/tmp/notes" to "none"
    Then the change fails with "wstat -- not in group"

    Examples:
      | table                    | printed                                                          | logged                                       |
      | "1:glenda::\n2\n"        | /adm/users:2: missing ':' after name\nload users: syntax error\n | load users: syntax error\nkeeping old table\n |
      | 1048576 bytes of "#"     | load users: file too big\n                                       |                                              |

  @FOG_HOST_004
  Scenario: The console cannot read a table that is gone
    Given the host has booted
    And the process has removed "/root/adm/users"
    When the console runs users
    Then the console prints "load users: file does not exist: '/root/adm/users'\n"

  @FOG_HOST_004
  Scenario: A store reamed before the host read its users has none, and each user is then in only its own group
    Given the host has booted
    And the process has removed "/root/adm/users"
    And the process has created "/tmp/notes" holding "kept"
    When the host restarts
    And the process changes the group of "/tmp/notes" to "sys"
    Then the change fails with "wstat -- not in group"
    When the process changes the group of "/tmp/notes" to "glenda"
    Then "/tmp/notes" has the group "glenda"

  @FOG_HOST_004
  Scenario: A store whose user table is broken does not boot, as gefs bails when it is not permissive
    Given the host has booted
    And "/root/adm/users" holds "1:glenda:bob:\n"
    When the host restarts
    Then the boot fails with "user does not exist"
    And the host's log says "/adm/users:1: leader bob does not exist\nuser table broken: user does not exist\n\tnot permissive: bailing\n"

  @FOG_HOST_005
  Scenario: The kernel tells the time by the host's clock
    Given the host's clock reads 1000 seconds since the epoch
    When the host boots
    Then the first 8 bytes of "/dev/bintime" are 1000000000000 nanoseconds

  @FOG_HOST_006
  Scenario: The host writes the installer's files at each boot, making the directories they need
    Given the host is to install "lib/greeting" holding "hello" and "sys/lib/new/user" holding "#!/bin/rc\necho new\n"
    When the host boots
    Then reading "/root/lib/greeting" gives "hello"
    And reading "/root/sys/lib/new/user" gives "#!/bin/rc\necho new\n"
    And "/root/sys/lib/new/user" has the mode 775
    And "/root/sys/lib/new" has the mode 775
    When the process creates "/root/sys/lib/new/user" holding "changed"
    And the host restarts
    Then reading "/root/sys/lib/new/user" gives "#!/bin/rc\necho new\n"

  @FOG_HOST_007
  Scenario: init runs the boot once the namespace is built, its descriptors 0 to 2 the console, and the host keeps how it ended
    Given the host is to run "boot one two" at boot, a program that writes its arguments to descriptors 1 and 2 and to 3, and exits "done"
    When the host boots
    Then the console shows "boot one two\nboot one two\n"
    And the boot status ends with "done"

  @FOG_HOST_007
  Scenario: A boot that runs nothing leaves the status empty
    When the host boots
    Then the boot status is empty

  @FOG_HOST_008
  Scenario: The kernel has the host's network, console and nvram
    Given the host's nvram holds "sealed"
    When the host boots
    Then reading "/dev/nvram" gives "sealed"
    And the kernel's network and console are the host's

  @FOG_HOST_009
  Scenario: The store is posted in /srv/boot, where an attach is authenticated through factotum
    When the host boots
    Then "/srv/boot" belongs to the host owner
    And authenticating on "/srv/boot" for "main" fails with "file does not exist: '/mnt/factotum'"
    And mounting "/srv/boot" on "/n" without authenticating fails with "attach -- unknown user or failed authentication"
