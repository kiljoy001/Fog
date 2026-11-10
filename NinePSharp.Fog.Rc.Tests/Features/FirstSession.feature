@fog_first_session
Feature: A first session on a Fog host booted from its gefs store
  Fog's host boots the kernel on its gefs store and runs the boot as a 9front CPU server does:
  the host owner's factotum, its key read from the host's sealed key store in place of the
  nvram, and then /rc/bin/cpurc, which mounts factotum, starts ndb/cs and auth/secstored, and
  runs aux/listen with /rc/bin/service, whose tcp17019 takes each call. When the host reams its
  store it installs these files, and /sys/lib/newuser, as 9front's installer copies them, with the
  site's /lib/ndb/local, which names the domain's auth server, and /adm/users as gefs reams it. A user is added as gefs(8) adds one: a line in /adm/users, which
  the store reads again, and /usr/$user made theirs. Their first session runs /sys/lib/newuser,
  which makes their directories and writes lib/profile, and each session after it reads that
  profile. Fog's newuser is 9front's with the steps for programs Fog does not have left out.
  /bin is the store's /bin, which the members of the group sys may write, so an application one
  session installs is in /bin for every session, and is kept when the host restarts.

  Background:
    Given a host whose store file is in a new directory, with the host owner "bootes" of the domain "fog"
    And the auth server for "fog" knows "glenda" with the password "secretpw"
    And the host has booted
    And "glenda" has been added as gefs adds a user, a member of "sys"

  @FOG_LOGIN_007
  Scenario: The host's boot leaves factotum, cs, secstored and listen running, as cpurc does
    When fog runs "-u glenda -c 'cat /dev/user; echo; ls /srv/factotum /net/cs'"
    Then fog prints "glenda\n/net/cs\n/srv/factotum"
    And the host's processes include "factotum", "cs", "secstored" and "listen"

  @FOG_LOGIN_007
  Scenario: A first session finds its home
    When fog runs "-u glenda -c 'echo $home; cd; pwd'"
    Then fog prints "/usr/glenda\n/usr/glenda"

  @FOG_LOGIN_007
  Scenario: newuser makes the user's directories and profile, and a later session reads the profile
    When fog runs "-u glenda -c /sys/lib/newuser"
    And fog runs "-u glenda -c 'ls -d $home/bin/rc $home/lib $home/tmp; echo $prompt(1)'"
    Then fog prints "/usr/glenda/lib\n/usr/glenda/tmp\n/usr/glenda/bin/rc\ncpu% "

  @FOG_LOGIN_007
  Scenario: newuser does not write a profile that is already there
    Given fog has run "-u glenda -c /sys/lib/newuser"
    When fog runs "-u glenda -c /sys/lib/newuser"
    Then fog prints "user directories already made"
    And fog exits 1

  @FOG_LOGIN_007
  Scenario: A first session finds the installed applications
    Given "/bin" on the store holds the application "app"
    When fog runs "-u glenda -c 'ls /bin/app'"
    Then fog prints "/bin/app"

  @FOG_LOGIN_007
  Scenario: An application installed in a session is still there after the host restarts
    Given fog's local directory holds "app.wasm" with "an application"
    And fog has run "-u glenda -c 'cp app.wasm /bin/app'" in that directory
    When the host stops and boots again on the same store
    And fog runs "-u glenda -c 'ls /bin/app'"
    Then fog prints "/bin/app"

  @FOG_LOGIN_007
  Scenario: A user not in sys cannot install an application
    Given "ken" has been added as gefs adds a user
    And the auth server for "fog" knows "ken" with the password "kenpw"
    And fog's local directory holds "app.wasm" with "an application"
    When fog runs "-u ken -c 'cp app.wasm /bin/app'" in that directory with $PASS "kenpw"
    Then fog prints "cp: can't create /bin/app: permission denied" on its standard error
    And fog exits 1
