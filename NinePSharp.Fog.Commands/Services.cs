#pragma warning disable SA1027 // the scripts are as 9front has them, indented with tabs

namespace NinePSharp.Fog.Commands;

// The scripts of 9front's /rc/bin/service that Fog's listener runs, as 9front has them, and the
// boot's, and newuser, as Fog has them: 9front's with what Fog does not have left out.
public static class Services
{
    // tcp17019: rcpu's and drawterm's calls, authenticated and encrypted by tlssrv, which runs the
    // script the client sends.
    public const string Tcp17019 = """
        #!/bin/rc
        if(~ $#* 3){
        	netdir=$3
        	remote=$2!`{cat $3/remote}
        }
        fn server {
        	~ $#remote 0 || echo -n $netdir $remote >/proc/$pid/args
        	rm -f /env/'fn#server'
        	. <{n=`{read} && ! ~ $#n 0 && read -c $n} >[2=1]
        }
        exec tlssrv -a /bin/rc -c server

        """;

    // The end of bootrc for a cpu server, and init's cpustart: the host owner's factotum, its key
    // from the nvram, and then cpurc. Fog's root is mounted, and its mount points made, before.
    public const string Bootrc = """
        service=cpu
        auth/factotum -n -sfactotum -S
        /rc/bin/cpurc

        """;

    // cpurc, run if service=cpu. Fog's devices are bound and bootrc made its mount points; it has no
    // usb, swap, partitions, ip configuration, dns, timesync or apm, and no cpurc.local or /cfg.
    // auth/secstored is started here, as 9front's /cfg/$sysname/cpurc starts it on an auth server.
    public const string Cpurc = """
        #!/bin/rc
        # this file is run if service=cpu

        # now that /mnt exists, mount factotum
        mount /srv/factotum /mnt/factotum factotum
        bind -q /mnt/factotum/factotum /mnt/factotum

        # cs sets sysname
        if(! test -e /srv/cs && ! test -e /net/cs)
        	ndb/cs
        sysname=`{cat /dev/sysname}
        if(~ $#sysname 0 || ~ $sysname ''){
        	sysname=cirno			# default
        	echo -n $sysname >/dev/sysname
        }
        prompt=($sysname^'# ' '	')

        auth/secstored

        serviced=/rc/bin/service
        aux/listen -q -d $serviced tcp

        """;

    // /sys/lib/newuser: Fog has no $cputype, mail, cron, gefs's other dataset or plumber, and its
    // profile starts none of the terminal's programs, so their lines are left out.
    public const string Newuser = """
        #!/bin/rc

        user=`{cat /dev/user}
        home=/usr/$user
        if(test -f $home/lib/profile){
        	echo user directories already made
        	exit no
        }
        cd $home
        x='$'
        mkdir bin bin/rc
        mkdir lib tmp
        chmod +t tmp
        bind -c $home/tmp /tmp

        cat > lib/profile <<!
        bind -qa $x^home/bin/rc /bin
        switch($x^service){
        case cpu
        	bind /mnt/term/dev/cons /dev/cons
        	bind -q /mnt/term/dev/consctl /dev/consctl
        	>[2] /dev/null {
        		cp /dev/sysname /mnt/term/dev/label
        	} 
        	bind -a /mnt/term/dev /dev
        	prompt=('cpu% ' '	')
        	fn cpu%{ $x^* }
        	if(! test -e /mnt/term/dev/wsys){
        		# call from drawterm
        		if(test -e /mnt/term/dev/secstore){
        			auth/factotum -n
        			read -m /mnt/term/dev/secstore >/mnt/factotum/ctl
        			echo >/mnt/term/dev/secstore
        		}
        		if not
        			auth/factotum
        	}
        case con
        	prompt=('cpu% ' '	')
        }
        !

        . lib/profile

        """;
}
