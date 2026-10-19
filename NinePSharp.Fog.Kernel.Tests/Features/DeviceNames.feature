@fog_kernel_device_names
Feature: A name starting with # begins at the device it names, as 9front's namec does
  After the #, a name gives a device's rune and the spec that device attaches with, up to the next
  slash, and the rest of the name is walked from the device's root: #c is the console, #d the
  descriptors, #e the environment group, #p the processes, #I the network, #/ the root device, and
  the store the kernel boots from is #R, which 9front reaches through /srv/boot instead. A
  rune that names no device fails "unknown device in # filename". A process whose namespace was
  forked with RFNOMNT may attach only the devices 9front leaves it, |, d, e, c and p, and any other
  fails "mount/attach disallowed". So the names of /lib/namespace, and libc's and libauth's uses
  of the console and environment by their names, work as they do on 9front.

  Background:
    Given a kernel booted for the host owner "glenda"
    And "/env/home" holds "/usr/glenda"
    And the process has "/dev/null" open

  @FOG_KERNEL_069
  Scenario Outline: Each device is named by its rune
    Then the names in "<name>" include "<entry>"

    Examples:
      | name | entry |
      | #c   | user  |
      | #d   | 0     |
      | #e   | home  |
      | #p   | 1     |
      | #I   | tcp   |
      | #/   | net   |
      | #R   | lib   |

  @FOG_KERNEL_069
  Scenario: A file under a device is reached by its name under the device's rune
    Then reading "#c/user" gives "glenda"
    And reading "#e/home" gives "/usr/glenda"

  @FOG_KERNEL_069
  Scenario: Binding a device by its name puts its files on a path
    When the process binds "#c" on "/n"
    Then reading "/n/user" gives "glenda"

  @FOG_KERNEL_069
  Scenario: A rune that names no device is refused
    When the process stats "#z/x"
    Then the call fails with "unknown device in # filename"

  @FOG_KERNEL_070
  Scenario: Under RFNOMNT only the devices 9front leaves may be attached by name
    Given the process has forked its namespace with RFNOMNT
    Then reading "#c/user" gives "glenda"
    When the process stats "#I"
    Then the call fails with "mount/attach disallowed"

  @FOG_KERNEL_069
  Scenario Outline: A walk that fails under a device names the device and the elements as far as the failure
    When the process stats "<name>"
    Then the call fails with "<error>"

    Examples:
      | name            | error                                    |
      | #c/nonesuch     | file does not exist: '#c/nonesuch'       |
      | #c/nonesuch/x   | file does not exist: '#c/nonesuch'       |
      | #c/user/x       | not a directory: '#c/user'               |

  @FOG_KERNEL_071
  Scenario: Each boot writes /lib/namespace, the namespace file newns builds a namespace from
    Then reading "/lib/namespace" gives the namespace file the kernel writes
