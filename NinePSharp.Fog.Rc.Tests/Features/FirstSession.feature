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

  # rc runs an application copied into /bin as it runs any program: exec knows WASM by its preamble,
  # and the application is a process of the session, with its arguments, its descriptors and a wait
  # message for rc. cp keeps the local file's mode, as 9front's does, so the copy is made executable.
  @FOG_LOGIN_006
  Scenario: An application installed from a session runs as a process of the session
    Given fog's local directory holds the WASM applications "echo-args.wasm" and "exit-3.wasm"
    When fog runs "-u glenda -c 'cp echo-args.wasm /bin/app; cp exit-3.wasm /bin/fails; /bin/app; chmod +x /bin/app /bin/fails; cmp echo-args.wasm /bin/app; app one two; fails; echo $status | sed ''s/ [0-9]+:/ N:/'''" in that directory
    Then fog prints "app\none\ntwo\nfails N: wasm-exit 3"
    And fog prints "/bin/app: permission denied: '/bin/app'" on its standard error

  @FOG_LOGIN_007
  Scenario: A user not in sys cannot install an application
    Given "ken" has been added as gefs adds a user
    And the auth server for "fog" knows "ken" with the password "kenpw"
    And fog's local directory holds "app.wasm" with "an application"
    When fog runs "-u ken -c 'cp app.wasm /bin/app'" in that directory with $PASS "kenpw"
    Then fog prints "cp: can't create /bin/app: permission denied" on its standard error
    And fog exits 1

  # Stock rcpu logs in to the host booted from its store: the listener's tlssrv authenticates the
  # call through the host owner's factotum, whose key -S read from the host's sealed key store, and
  # auth_chuid spends the capability factotum made, so the session is the user's.
  @FOG_LOGIN_008
  Scenario: Stock 9front rcpu logs in through the host owner's factotum
    When 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'cat /dev/user'"
    Then 9front prints "glenda"

  # A ticket the key does not open decrypts to no ticket at all, so dp9ik's server finds it is not
  # one for the server, and factotum logs the failure, as 9front's does without -d.
  @FOG_LOGIN_008
  Scenario: A call whose ticket the host owner's key does not open is refused, and the host owner's factotum logs why
    When the host stops and boots again, its sealed key made from a password the auth server does not know
    And 9front runs "rcpu -h tcp!fog!17019 -u glenda -c 'cat /dev/user'"
    Then 9front prints "tlsclient: auth_proxy: auth_proxy short read" on its standard error
    And the host owner's factotum logged "failure auth server protocol botch"

  # The profile starts factotum for a call from a terminal with no window system, as fog's is, and
  # factotum's secstore asks on the terminal for the password; the session that ran newuser was
  # asked too, and was given nothing.
  @FOG_LOGIN_009
  Scenario: A session's factotum takes the user's keys from secstore
    Given glenda's secstore on the host holds the file "factotum" with "key proto=pass server=example user=glenda !password=x"
    And fog has run "-u glenda -c /sys/lib/newuser"
    When fog runs "-u glenda -c 'cat /mnt/factotum/ctl'" and is given glenda's secstore password
    Then fog prints "key proto=pass server=example user=glenda !password?"
    And fog's terminal shows "secstore password: \nsecstore password: \n"

  # drawterm logs in with the dp9ik key in the file it fetches from secstore, and has served no
  # /dev/secstore since 2016, forgetting the file once it has logged in; so the profile starts
  # factotum, whose secstore asks on drawterm's console for the password again, and says on it
  # whose store it logged in to.
  @FOG_LOGIN_009
  Scenario: A drawterm session's factotum takes the user's keys from secstore
    Given glenda's secstore on the host holds the file "factotum" with "key proto=dp9ik dom=fog user=glenda !password=secretpw\nkey proto=pass server=example user=glenda !password=x"
    And fog has run "-u glenda -c /sys/lib/newuser"
    When drawterm runs "-u glenda -c 'cat /mnt/factotum/ctl'" and is given glenda's secstore password twice
    Then drawterm prints "secstore password: \nsecstore password: \nsecstore\nkey proto=dp9ik dom=fog user=glenda !password?\nkey proto=pass server=example user=glenda !password?"

  @FOG_HOST_009
  Scenario: A session may mount the store from /srv/boot, authenticated by its factotum, but may not give a file away
    When fog runs "-u glenda -c 'mount /srv/boot /n/u; ls -d /n/u/usr/glenda; chgrp -u bootes /n/u/usr/glenda'"
    Then fog prints "/n/u/usr/glenda"
    And fog prints "chgrp: can't wstat /n/u/usr/glenda: wstat -- not owner" on its standard error

  @FOG_HOST_009
  Scenario: Only a member of adm may mount the store with the permit
    When fog runs "-u glenda -c 'mount /srv/boot /n/u %main'"
    Then fog prints "mount: mount /n/u: permission denied" on its standard error

  @FOG_HOST_009
  Scenario: A mount of the store that does not authenticate is refused
    When fog runs "-u glenda -c 'mount -n /srv/boot /n/u'"
    Then fog prints "mount: mount /n/u: attach -- unknown user or failed authentication" on its standard error

  @FOG_HOST_009
  Scenario: mount -k narrows the key factotum authenticates with, and mount says when factotum has none, leaving gefs's factotum out of phase
    When fog runs "-u glenda -c 'mount -k ''dom=elsewhere'' /srv/boot /n/u'"
    Then fog prints "mount: auth_proxy: auth_proxy rpc write: no key matches dom=elsewhere proto=dp9ik dom=fog role=client" on its standard error
    And fog prints "mount: mount /n/u: auth phase error" on its standard error

  @FOG_HOST_009
  Scenario: A namespace file mounts the store authenticated through the factotum newns opened
    When fog runs "-u glenda -c 'echo ''mount /srv/boot /n/b'' >$home/ns; auth/newns -a -n $home/ns ls -d /n/b/usr/glenda'"
    Then fog prints "/n/b/usr/glenda"

  # chgrp without -u changes only the group; the owner it leaves would be a gift gefs refuses.
  @FOG_HOST_009
  Scenario: chgrp gives a file of the user's to a group they are in, keeping its owner
    When fog runs "-u glenda -c 'cd; >f; chgrp sys f; x=`{ls -l f}; echo $x(4) $x(5)'"
    Then fog prints "glenda sys"

  # newns opens factotum's rpc on the lowest free descriptor, which is 0 when its input is closed,
  # and authenticates through it all the same.
  @FOG_HOST_009
  Scenario: newns authenticates through factotum's rpc when it opens on descriptor 0
    When fog runs "-u glenda -c 'echo ''mount /srv/boot /n/b'' >$home/ns; auth/newns -a -n $home/ns ls -d /n/b/usr/glenda <[0=]'"
    Then fog prints "/n/b/usr/glenda"
