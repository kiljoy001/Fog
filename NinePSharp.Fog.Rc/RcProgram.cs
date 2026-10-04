using NinePSharp.Fog.Kernel;

namespace NinePSharp.Fog.Rc;

// rc as a program a kernel runs, and the /rc/lib/rcmain it bootstraps from, 9front's.
public static class RcProgram
{
    public const string Rcmain = @"# rcmain: Plan 9 version
if(~ $#home 0) home=/
if(~ $#ifs 0) ifs=' 	
'
switch($#prompt){
case 0
	prompt=('% ' '	')
case 1
	prompt=($prompt '	')
}
if(~ $rcname ?.out) prompt=('broken! ' '	')
if(flag p) path=/bin
if not{
	finit
	if(~ $#path 0) path=(/bin .)
}
fn sigexit
if(! ~ $#cflag 0){
	if(flag l){
		if(/bin/test -r /rc/lib/rcmain.local) . /rc/lib/rcmain.local
		if(/bin/test -r $home/lib/profile) . $home/lib/profile
	}
	status=''
	eval $cflag
}
if not if(flag i){
	if(flag l){
		if(/bin/test -r /rc/lib/rcmain.local) . /rc/lib/rcmain.local
		if(/bin/test -r $home/lib/profile) . $home/lib/profile
	}
	status=''
	if(! ~ $#* 0) . $*
	. -i /fd/0
}
if not if(~ $#* 0) . /fd/0
if not{
	status=''
	. $*
}
";

    public static Task MainAsync(Process process, IReadOnlyList<string> argv) => RcShell.MainAsync(process, argv);
}
