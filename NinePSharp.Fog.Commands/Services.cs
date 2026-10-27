#pragma warning disable SA1027 // the scripts are as 9front has them, indented with tabs

namespace NinePSharp.Fog.Commands;

// The scripts of 9front's /rc/bin/service that Fog's listener runs, as 9front has them.
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
}
