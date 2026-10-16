@fog_kernel_boot
Feature: The kernel boots its root as 9front's bootrc and /lib/namespace build it
  The root is the root device, #/, a directory of the mount points 9front's devroot makes: bin, dev,
  env, fd, mnt, n, net, net.alt, proc, rc, root, srv and shr. The boot store is mounted on /root
  and bound after the root device on /, as bootrc's "mount /srv/boot /root" and "bind -a $rootdir /"
  do, so the store's own directories, such as /adm, /lib, /tmp and /usr, are reached through / and
  / itself takes no new files. The kernel's devices are then bound as /lib/namespace binds them, the
  console #c on /dev, #d on /fd, and #e on /env with creation, and the store's /rc on /rc, as
  "bind $rootdir/rc /rc" puts rc's library there. Fog has no $cputype, so in place of
  "bind /$cputype/bin /bin" the store's /bin is bound on /bin with creation, and a program copied
  there is kept by the store. Each boot makes the store's adm, bin, lib, rc, tmp and usr when they
  are missing and writes the kernel's managed programs into its /bin.

  Background:
    Given a kernel booted on a store

  @FOG_KERNEL_038
  Scenario: The root lists the root device's mount points and the store's directories
    Then the names in "/" are "adm bin dev env fd lib mnt n net net.alt proc rc root shr srv tmp usr"

  @FOG_KERNEL_038
  Scenario: The store is mounted on /root
    When the process creates "/tmp/notes" holding "kept"
    Then reading "/root/tmp/notes" gives "kept"

  @FOG_KERNEL_038
  Scenario: A stat of the root or of a mount point is named by the path's last element
    Then "/" is named "/"
    And "/mnt" is named "mnt"

  @FOG_KERNEL_038
  Scenario: A mount point of the root device holds nothing of its own
    Then the names in "/mnt" are ""

  @FOG_KERNEL_038
  Scenario: The root device opens for reading only
    When the process opens "/mnt" for writing
    Then the call fails with "permission denied"

  @FOG_KERNEL_039
  Scenario: The root takes no new files
    When the process creates "/notes" holding "lost"
    Then the call fails with "mounted directory forbids creation: '/notes'"

  @FOG_KERNEL_039
  Scenario: The root is a mount point, so it is not renamed
    When the process renames "/" to "root"
    Then the call fails with "is a mount point: '/'"

  @FOG_KERNEL_039
  Scenario: The store's mount takes no creation, so binding it with creation is an inconsistent mount
    When the process binds "/root" onto "/root" with creation
    Then the call fails with "inconsistent mount"

  @FOG_KERNEL_040
  Scenario: A program copied into /bin is written to the store's /bin
    When the process creates "/bin/app" holding "an application"
    Then reading "/root/bin/app" gives "an application"

  @FOG_KERNEL_041
  Scenario: The kernel's devices are bound as /lib/namespace binds them
    Then "/dev" is a directory of device "c"
    And "/dev/null" is a file of device "c"
    And "/fd" is a directory of device "d"
    And "/env" is a directory of device "e"

  @FOG_KERNEL_041
  Scenario: rc's library is the store's /rc
    When the process creates "/root/rc/rcmain" holding "a profile"
    Then reading "/rc/rcmain" gives "a profile"

  @FOG_KERNEL_041
  Scenario: The environment takes new variables
    When the process creates "/env/home" holding "/usr/glenda"
    Then reading "/env/home" gives "/usr/glenda"

  @FOG_KERNEL_042
  Scenario: Each boot writes the managed programs into the store's /bin
    Given the store's "/bin/cat" holds "an old cat"
    When a kernel with the program "cat" boots on the store
    Then reading "/root/bin/cat" gives the managed program "cat"

  @FOG_KERNEL_042
  Scenario: A managed program whose name has directories is written under them in the store's /bin
    When a kernel with the program "aux/listen1" boots on the store
    Then reading "/root/bin/aux/listen1" gives the managed program "aux/listen1"
    And "/bin/aux" is a directory of device "R"
