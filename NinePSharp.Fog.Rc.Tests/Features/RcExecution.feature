@fog_rc_execution
Feature: rc runs scripts as 9front rc does
  rc runs on Fog's kernel as a program, bootstrapped by 9front's /rc/lib/rcmain, with echo and cat
  in /bin. A script runs with no input and its output and errors on one pipe, and prints what 9front
  rc prints for it.

  Background:
    Given a kernel whose /bin holds rc, echo and cat

  @FOG_RC_RUN_001
  Scenario: A simple command runs a program with its arguments
    When rc runs the script
      """
      echo hello world
      """
    Then it prints
      """
      hello world
      """

  @FOG_RC_RUN_002
  Scenario: Variables hold lists, counted, subscripted and joined
    When rc runs the script
      """
      a=(x y z)
      echo $a
      echo $#a
      echo $a(2) $a(2-) $a(1-2)
      echo $"a
      b=()
      echo $#b
      """
    Then it prints
      """
      x y z
      3
      y y z x y
      x y z
      0
      """

  @FOG_RC_RUN_003
  Scenario: Concatenation joins words and lists, with free carets
    When rc runs the script
      """
      a=x
      echo $a^y pre$a (a b)^(1 2) (a b)^c
      """
    Then it prints
      """
      xy prex a1 b2 ac bc
      """

  @FOG_RC_RUN_003
  Scenario: Lists of different lengths do not concatenate
    When rc runs the script
      """
      echo (a b)^(1 2 3)
      echo not reached
      """
    Then it prints
      """
      /tmp/s:1: mismatched list lengths in concatenation
      """

  @FOG_RC_RUN_004
  Scenario: if, if not, while, for and switch
    When rc runs the script
      """
      if(~ a a) echo yes
      if not echo no
      if(~ a b) echo yes
      if not echo no
      for(i in 1 2 3) echo $i
      x=()
      while(! ~ $#x 2) x=($x x)
      echo $x
      switch(b){
      case a
          echo a
      case b c
          echo b or c
      case *
          echo other
      }
      """
    Then it prints
      """
      yes
      no
      1
      2
      3
      x x
      b or c
      """

  @FOG_RC_RUN_005
  Scenario: && and || run on the status, and ~ sets it
    When rc runs the script
      """
      ~ a b || echo no match
      ~ a a && echo match
      ~ a b
      echo $status
      """
    Then it prints
      """
      no match
      match
      no match
      """

  @FOG_RC_RUN_006
  Scenario: Functions take their arguments as $*
    When rc runs the script
      """
      fn f { echo $#* $1 $2 }
      f a b c
      whatis f
      fn f
      whatis f
      """
    Then it prints
      """
      3 a b
      fn f {
      	echo $#* $1 $2
      }
      """

  @FOG_RC_RUN_007
  Scenario: A pipe connects a command's output to the next one's input
    When rc runs the script
      """
      echo through the pipe | cat
      echo $status
      """
    Then it prints
      """
      through the pipe

      """

  @FOG_RC_RUN_008
  Scenario: Backquotes substitute a command's output as words
    When rc runs the script
      """
      x=`{echo a b c}
      echo $#x $x(2)
      """
    Then it prints
      """
      3 b
      """

  @FOG_RC_RUN_009
  Scenario: Redirections write, append and read files
    When rc runs the script
      """
      echo one >/tmp/o
      echo two >>/tmp/o
      cat </tmp/o
      echo err >[1=2]
      """
    Then it prints
      """
      one
      two
      err
      """

  @FOG_RC_RUN_010
  Scenario: A here document substitutes variables unless its tag is quoted
    When rc runs the script
      """
      v=value
      cat <<EOF
      $v and $$
      EOF
      cat <<'EOF'
      $v
      EOF
      """
    Then it prints
      """
      value and $
      $v
      """

  @FOG_RC_RUN_011
  Scenario: A missing command is reported with the error of the last place tried, its status the child's wait message
    When rc runs the script
      """
      nonesuch
      ~ $status 'rc '[0-9]*': file does not exist: ''./nonesuch''' && echo status ok
      """
    Then it prints
      """
      /tmp/s:1: nonesuch: file does not exist: './nonesuch'
      status ok
      """

  @FOG_RC_RUN_012
  Scenario: @ runs commands in a subshell
    When rc runs the script
      """
      x=outer
      @{ x=inner; echo $x }
      echo $x
      """
    Then it prints
      """
      inner
      outer
      """

  @FOG_RC_RUN_013
  Scenario: eval, shift and builtin whatis
    When rc runs the script
      """
      eval echo evaluated
      fn g { shift; echo $* }
      g a b c
      whatis cd echo
      """
    Then it prints
      """
      evaluated
      b c
      builtin cd
      /bin/echo
      """

  @FOG_RC_RUN_014
  Scenario: Globs match the names in directories, sorted, and a glob matching nothing is itself
    When rc runs the script
      """
      echo a >/tmp/a.c; echo b >/tmp/b.c; echo x >/tmp/x.h
      echo /tmp/*.c
      echo /tmp/[ab].c /tmp/?.h /tmp/[~a].c
      echo /tmp/*.nonesuch
      echo '/tmp/*.c'
      """
    Then it prints
      """
      /tmp/a.c /tmp/b.c
      /tmp/a.c /tmp/b.c /tmp/x.h /tmp/b.c
      /tmp/*.nonesuch
      /tmp/*.c
      """

  @FOG_RC_RUN_015
  Scenario: A local assignment holds only for its command
    When rc runs the script
      """
      x=global
      fn show { echo $x }
      x=local show
      show
      """
    Then it prints
      """
      local
      global
      """

  @FOG_RC_RUN_015
  Scenario: Variables and functions are exported to the commands rc runs
    When rc runs the script
      """
      x=exported
      fn f { echo in f }
      rc -c 'echo $x; f'
      y=just-for-it rc -c 'echo $y'
      """
    Then it prints
      """
      exported
      in f
      just-for-it
      """

  @FOG_RC_RUN_016
  Scenario: Redirections close descriptors and open files for reading and writing
    When rc runs the script
      """
      echo hello >/tmp/rw
      cat <>/tmp/rw
      echo closed >[1=]
      echo after
      """
    Then it prints
      """
      hello
      echo: write error: fd out of range or not open
      after
      """

  @FOG_RC_RUN_017
  Scenario Outline: The interpreter's errors stop a script and name its line
    When rc runs the script
      """
      <line>
      echo not reached
      """
    Then it prints
      """
      /tmp/s:1: <error>
      """

    Examples:
      | line                         | error                                                   |
      | echo x >(/tmp/a /tmp/b)      | > requires singleton                                    |
      | x=() ; echo x >$x            | > requires file                                         |
      | x=(a b); echo $$x            | $ variable name not singleton!                          |
      | x=(a b); echo $#$x           | $# variable name not singleton!                         |
      | x=(a b); $x=1                | = variable name not singleton!                          |
      | x=(a b); $x=1 echo           | local variable name must be singleton                   |
      | x=(); $x                     | empty argument list                                     |
      | builtin                      | builtin: empty argument list                            |
      | echo ()^x                    | null list in concatenation                              |
      | cat </env/nonesuch           | < can't open: /env/nonesuch: file does not exist: '/env/nonesuch' |
      | echo >/nonesuch/x            | > can't create: /nonesuch/x: file does not exist: '/nonesuch'     |
      | eval                         | Usage: eval cmd ...                                     |
      | whatis                       | Usage: whatis name ...                                  |

  @FOG_RC_RUN_018
  Scenario: exit ends rc with a status, and exec runs a command in its place
    When rc runs the script
      """
      rc -c 'exit oops'
      ~ $status 'rc '[0-9]*': oops' && echo exited with oops
      rc -c 'exec echo instead; echo not reached'
      exec echo last
      echo not reached
      """
    Then it prints
      """
      exited with oops
      instead
      last
      """

  @FOG_RC_RUN_019
  Scenario: shift drops arguments, and . runs a file with arguments
    When rc runs the script
      """
      echo 'echo $0 $* $#*; shift 2; echo $*' >/tmp/dot
      . /tmp/dot a b c d
      shift x y
      echo $status
      """
    Then it prints
      """
      /tmp/dot a b c d 4
      c d
      Usage: shift [n]
      shift usage
      """

  @FOG_RC_RUN_020
  Scenario: & runs a command without waiting, and wait waits for it
    When rc runs the script
      """
      echo background >/tmp/bg &
      ~ $apid [0-9]* && echo apid set
      wait $apid
      cat /tmp/bg
      """
    Then it prints
      """
      apid set
      background
      """

  @FOG_RC_RUN_021
  Scenario: <{} gives a command's output as a file
    When rc runs the script
      """
      cat <{echo from a pipe}
      """
    Then it prints
      """
      from a pipe
      """

  @FOG_RC_RUN_022
  Scenario: A pipeline's status joins its commands' statuses
    When rc runs the script
      """
      rc -c 'exit a' | rc -c 'exit b'
      ~ $status 'rc '[0-9]*': a|rc '[0-9]*': b' && echo joined
      """
    Then it prints
      """
      joined
      """

  @FOG_RC_RUN_023
  Scenario: flag tells whether a flag is set, and sets and clears it
    When rc runs the script
      """
      flag z
      echo $status
      flag z +
      flag z && echo set
      flag z -
      flag z
      echo $status
      """
    Then it prints
      """
      flag not set
      set
      flag not set
      """

  @FOG_RC_RUN_024
  Scenario: cd changes the directory of rc and the commands it runs
    When rc runs the script
      """
      cd /tmp
      echo here >f
      cat /tmp/f
      cd /nonesuch
      echo $status
      """
    Then it prints
      """
      here
      Can't cd /nonesuch: file does not exist: '/nonesuch'
      can't cd
      """

  @FOG_RC_RUN_025
  Scenario: if not must follow an if
    When rc runs the script
      """
      if not echo x
      echo not reached
      """
    Then it prints
      """
      /tmp/s:2: `if not' does not follow `if(...)'
      """

  @FOG_RC_RUN_025
  Scenario: A switch must start with a case
    When rc runs the script
      """
      switch(a){ echo x }
      echo not reached
      """
    Then it prints
      """
      /tmp/s:2: case missing in switch
      """

  @FOG_RC_RUN_026
  Scenario: while with an empty condition loops until something leaves it
    When rc runs the script
      """
      @{ x=(); while() { x=($x x); if(~ $#x 3) exit } }
      echo done
      """
    Then it prints
      """
      done
      """

  @FOG_RC_RUN_026
  Scenario: for without in loops over $*
    When rc runs the script
      """
      fn f { for(i) echo $i }
      f a b
      """
    Then it prints
      """
      a
      b
      """

  @FOG_RC_RUN_027
  Scenario: rc -e exits when a command fails
    When rc runs the script
      """
      rc -e -c 'echo a; ~ a b; echo not reached'
      ~ $status 'rc '[0-9]*': no match' && echo stopped
      """
    Then it prints
      """
      a
      stopped
      """

  @FOG_RC_RUN_028
  Scenario: A backquote may split at bytes it names
    When rc runs the script
      """
      x=`:{echo a:b:c}
      echo $#x $x(1) $x(2)
      """
    Then it prints
      """
      3 a b
      """

  @FOG_RC_RUN_029
  Scenario: Globs match UTF-8 characters and classes of them
    When rc runs the script
      """
      echo >/tmp/ä.u; echo >/tmp/β.u; echo >/tmp/ab.u
      echo /tmp/?.u
      echo /tmp/[α-ω].u /tmp/[~α-ω].u
      """
    Then it prints
      """
      /tmp/ä.u /tmp/β.u
      /tmp/β.u /tmp/ä.u
      """

  @FOG_RC_RUN_030
  Scenario: whatis prints values, quoted as rc reads them
    When rc runs the script
      """
      x='a b'; y=(1 'two words' ''); z=plain
      whatis x y z
      """
    Then it prints
      """
      x='a b'
      y=(1 'two words' '')
      z=plain
      """

  @FOG_RC_RUN_031
  Scenario: sigexit runs when rc exits
    When rc runs the script
      """
      fn sigexit { echo bye }
      echo hello
      """
    Then it prints
      """
      hello
      bye
      """

  @FOG_RC_RUN_032
  Scenario: A here document substitutes arguments and stops a name at a caret
    When rc runs the script
      """
      fn f {
      cat <<EOF
      $1 $2^x $3 $#*
      EOF
      }
      f a b
      """
    Then it prints
      """
      a bx  #*
      """

  @FOG_RC_RUN_033
  Scenario: A value's count and a missing value
    When rc runs the script
      """
      fn f { echo $#1 $#3 }
      f a b
      echo $"nonesuch
      """
    Then it prints
      """
      1 0

      """

  @FOG_RC_RUN_034
  Scenario: A command may read from several <{}
    When rc runs the script
      """
      cat <{echo a} <{echo b}
      """
    Then it prints
      """
      a
      b
      """

  @FOG_RC_RUN_035
  Scenario Outline: The builtins report misuse
    When rc runs the script
      """
      <line>
      echo $status
      """
    Then it prints
      """
      <output>
      """

    Examples:
      | line          | output                                          |
      | cd a b        | Usage: cd [directory]\ncan't cd                 |
      | home=(); cd   | Can't cd -- $home empty\ncan't cd               |
      | flag a b c    | /tmp/s:1: Usage: flag [letter] [+-]             |
      | wait a b      | /tmp/s:1: Usage: wait [pid]                     |
      | . -z x        | /tmp/s:1: Usage: . [-biq] file [arg ...]        |
      | .             | /tmp/s:1: Usage: . [-biq] file [arg ...]        |
      | . /nonesuch   | /tmp/s:1: . can't open: /nonesuch: file does not exist: '/nonesuch' |
      | exec          | /tmp/s:1: exec: empty argument list             |
      | rfork z       | Usage: rfork [fnesFNEm]\nrfork usage            |
      | rfork fF      | /bin/rc: rfork failed\nrfork failed             |

  @FOG_RC_RUN_036
  Scenario: cd without an argument goes to $home, and $cdpath finds directories
    When rc runs the script
      """
      home=/tmp
      cd
      echo at home >f
      cat /tmp/f
      cdpath=(/ /tmp)
      cd env
      """
    Then it prints
      """
      at home
      /env
      """

  @FOG_RC_RUN_037
  Scenario: exit with more than a status complains and exits anyway
    When rc runs the script
      """
      rc -c 'exit a b'
      ~ $status 'rc '[0-9]*': a' && echo exited
      """
    Then it prints
      """
      Usage: exit [status]
      Exiting anyway
      exited
      """

  @FOG_RC_RUN_038
  Scenario Outline: rc's flags are checked as getflags does
    When rc runs the script
      """
      rc <flags>
      """
    Then it prints
      """
      <error>
      Usage: rc [-srdiIlxebpvV] [-c arg] [-m command] [file [arg ...]]
      """

    Examples:
      | flags | error                        |
      | -z    | Illegal flag -z              |
      | -c    | Flag -c: too few arguments   |
      | -ee   | Flag -e: set twice           |

  @FOG_RC_RUN_038
  Scenario: rc -p uses /bin alone as its path, and a flag's argument may follow it in the same word
    When rc runs the script
      """
      rc -p '-cecho $path'
      """
    Then it prints
      """
      /bin
      """

  @FOG_RC_RUN_039
  Scenario: rc -s prints a failed status before reading the next command
    When rc runs the script
      """
      echo '~ a b' >/tmp/t
      echo 'echo next' >>/tmp/t
      rc -s /tmp/t
      """
    Then it prints
      """
      status='flag not set'
      status='flag not set'
      status='no match'
      next
      """

  @FOG_RC_RUN_040
  Scenario: wait with no argument waits for every child
    When rc runs the script
      """
      echo late >/tmp/w &
      wait
      cat /tmp/w
      """
    Then it prints
      """
      late
      """

  @FOG_RC_RUN_041
  Scenario: Globs match across directories and match names starting with a dot
    When rc runs the script
      """
      mkdir /tmp/g /tmp/g/d1 /tmp/g/d2 /tmp/g/.h
      echo >/tmp/g/d1/f; echo >/tmp/g/d2/f; echo >/tmp/g/d2/x; echo >/tmp/g/.dot
      echo /tmp/g/*
      echo /tmp/g/.*
      echo /tmp/g/d*/f
      echo /tmp/g/*/
      echo /tmp//g/d1/*
      """
    Then it prints
      """
      /tmp/g/.dot /tmp/g/.h /tmp/g/d1 /tmp/g/d2
      /tmp/g/.dot /tmp/g/.h
      /tmp/g/d1/f /tmp/g/d2/f
      /tmp/g/.h/ /tmp/g/d1/ /tmp/g/d2/
      /tmp//g/d1/f
      """

  @FOG_RC_RUN_042
  Scenario: mkdir makes directories, and with -p the directories along the way
    When rc runs the script
      """
      mkdir /tmp/m /tmp/m
      ~ $status 'mkdir '[0-9]*': error' && echo error status
      mkdir -p /tmp/p/q/r
      echo >/tmp/p/q/r/f && echo made
      mkdir -m 700 /tmp/n && echo with mode
      mkdir -z
      mkdir /nonesuch/x
      """
    Then it prints
      """
      mkdir: /tmp/m already exists
      error status
      made
      with mode
      usage: mkdir [-p] [-m mode] dir...
      mkdir: can't create /nonesuch/x: file does not exist: '/nonesuch'
      """

  @FOG_RC_RUN_043
  Scenario: rc -i prompts for each command and for each line that continues one
    When rc -i reads the script
      """
      echo one
      if(~ a a) {
      echo two
      }
      echo (
      echo after error
      """
    Then it prints, ending with a prompt
      """
      % one
      % 		two
      % /fd/0:6: syntax error
      % after error
      % 
      """

  @FOG_RC_RUN_044
  Scenario: rc -r traces each instruction with its stacks, as 9front rc does
    When rc runs the script
      """
      rc -r -c 'x=(a b); fn f { echo $#* $1 }; switch($x(1)){case a*; y=$"x}; ~ $y *b && x=(); while(! ~ $#x 2) x=($x 1); for(i in $x) z=$i; if(~ $z 1) y=one; if not y=other; f $z $y'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('x=(a b); fn f { echo $#* $1 }; switch($x(1)){case a*; y=$"x}; ~ $y *b && x=(); while(! ~ $#x 2) x=($x 1); for(i in $x) z=$i; if(~ $z 1) y=one; if not y=other; f $z $y')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval 'x=(a b); fn f { echo $#* $1 }; switch($x(1)){case a*; y=$"x}; ~ $y *b && x=(); while(! ~ $#x 2) x=($x 1); for(i in $x) z=$i; if(~ $z 1) y=one; if not y=other; f $z $y')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 7 Xword (b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 9 Xmark (a b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 10 Xword () (a b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 12 Xassign (x) (a b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 13 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 14 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 16 Xfn (f)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 34 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 35 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 36 Xword () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 38 Xmark (x) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 39 Xword () (x) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 41 F (1) (x) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 42 Xqw (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 43 Xjump (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 47 Xmark (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 48 Xword () (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 50 Xcase ('a\u0001*') (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 52 Xmark (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 53 Xmark () (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 54 Xmark () () (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 55 Xword () () () (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 57 Xdol (x) () () (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 58 Xqw (a b) () (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 59 Xpush ('a b') () (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 60 Xmark ('a b') (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 61 Xword () ('a b') (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 63 Xassign (y) ('a b') (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 64 Xjump (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 45 Xjump (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 66 Xpopm (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 67 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 68 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 70 Xmark ('\u0001*b')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 71 Xmark () ('\u0001*b')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 72 Xword () () ('\u0001*b')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 74 Xdol (y) () ('\u0001*b')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 75 Xqw ('a b') ('\u0001*b')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 76 Xmatch ('a b') ('\u0001*b')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 77 Xtrue
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 79 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 80 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 81 Xword () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 83 Xassign (x) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 84 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 85 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 87 Xmark (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 88 Xmark () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 89 Xword () () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 91 Xcount (x) () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 92 Xqw (0) (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 93 Xmatch (0) (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 94 Xbang
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 95 Xtrue
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 97 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 98 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 100 Xmark (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 101 Xword () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 103 Xdol (x) (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 104 Xmark (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 105 Xword () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 107 Xassign (x) (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 108 Xjump
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 84 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 85 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 87 Xmark (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 88 Xmark () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 89 Xword () () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 91 Xcount (x) () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 92 Xqw (1) (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 93 Xmatch (1) (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 94 Xbang
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 95 Xtrue
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 97 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 98 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 100 Xmark (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 101 Xword () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 103 Xdol (x) (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 104 Xmark (1 1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 105 Xword () (1 1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 107 Xassign (x) (1 1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 108 Xjump
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 84 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 85 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 87 Xmark (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 88 Xmark () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 89 Xword () () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 91 Xcount (x) () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 92 Xqw (2) (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 93 Xmatch (2) (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 94 Xbang
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 95 Xtrue
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 110 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 111 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 112 Xword () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 114 Xdol (x) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 115 Xmark (1 1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 116 Xmark () (1 1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 117 Xword () () (1 1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 119 Xlocal (i) () (1 1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 120 Xfor (1 1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 122 Xmark (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 123 Xmark () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 124 Xword () () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 126 Xdol (i) () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 127 Xmark (1) (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 128 Xword () (1) (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 130 Xassign (z) (1) (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 131 Xjump (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 120 Xfor (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 122 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 123 Xmark () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 124 Xword () () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 126 Xdol (i) () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 127 Xmark (1) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 128 Xword () (1) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 130 Xassign (z) (1) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 131 Xjump ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 120 Xfor ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 133 Xunlocal
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 134 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 135 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 137 Xmark (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 138 Xmark () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 139 Xword () () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 141 Xdol (z) () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 142 Xqw (1) (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 143 Xmatch (1) (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 144 Xif
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 146 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 147 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 149 Xmark (one)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 150 Xword () (one)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 152 Xassign (y) (one)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 153 Xwastrue
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 154 Xifnot
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 163 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 164 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 165 Xword () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 167 Xdol (y) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 168 Xmark (one)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 169 Xword () (one)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 171 Xdol (z) (one)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 172 Xword (1 one)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 174 Xsimple (f 1 one)
      /rc/lib/rcmain:24 *eval* pid N cycle C 19 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 21 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 22 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 23 Xword () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 25 Xdol (1) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 26 Xmark (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 27 Xword () (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 29 Xcount ('*') (1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 30 Xword (2 1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 32 Xsimple (echo 2 1)
      2 1
      """

  @FOG_RC_RUN_045
  Scenario: rc -r traces it as 9front rc does: A here document's text is substituted from arguments and variables
    When rc runs the script
      """
      rc -r -c 'fn f { cat <<EOF
      $1 $2^x $#* $$ $3
      EOF
      }; f a b'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('fn f { cat <<EOF
      $1 $2^x $#* $$ $3
      EOF
      }; f a b')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval 'fn f { cat <<EOF
      $1 $2^x $#* $$ $3
      EOF
      }; f a b')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 7 Xfn (f)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 21 Xsrcline
      /rc/lib/rcmain:24 *eval*:4 pid N cycle C 23 Xmark
      /rc/lib/rcmain:24 *eval*:4 pid N cycle C 24 Xword ()
      /rc/lib/rcmain:24 *eval*:4 pid N cycle C 26 Xword (b)
      /rc/lib/rcmain:24 *eval*:4 pid N cycle C 28 Xword (a b)
      /rc/lib/rcmain:24 *eval*:4 pid N cycle C 30 Xsimple (f a b)
      /rc/lib/rcmain:24 *eval* pid N cycle C 10 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 12 Xhere
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 15 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 16 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 18 Xsimple (cat)
      a bx #* $ 
      """

  @FOG_RC_RUN_046
  Scenario: rc -r traces it as 9front rc does: Redirections are pushed and popped around a builtin
    When rc runs the script
      """
      rc -r -c 'fn g {}; whatis g >/tmp/o >[2=1] >[3=]; whatis g >>/tmp/o; cat <>/tmp/o'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('fn g {}; whatis g >/tmp/o >[2=1] >[3=]; whatis g >>/tmp/o; cat <>/tmp/o')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval 'fn g {}; whatis g >/tmp/o >[2=1] >[3=]; whatis g >>/tmp/o; cat <>/tmp/o')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 7 Xfn (g)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 13 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 14 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 16 Xwrite (/tmp/o)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 18 Xdup
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 21 Xclose
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 23 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 24 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 26 Xword (g)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 28 Xsimple (whatis g)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 29 Xpopredir
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 30 Xpopredir
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 31 Xpopredir
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 32 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 33 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 35 Xappend (/tmp/o)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 37 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 38 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 40 Xword (g)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 42 Xsimple (whatis g)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 43 Xpopredir
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 44 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 45 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 47 Xrdwr (/tmp/o)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 49 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 50 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 52 Xsimple (cat)
      fn g {
      	
      }
      fn g {
      	
      }
      """

  @FOG_RC_RUN_047
  Scenario: rc -r traces it as 9front rc does: An error unwinds the threads that are not reading commands
    When rc runs the script
      """
      rc -r -c 'fn f { x=(a b); whatis $$x }; f'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('fn f { x=(a b); whatis $$x }; f')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval 'fn f { x=(a b); whatis $$x }; f')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 7 Xfn (f)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 32 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 33 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 35 Xsimple (f)
      /rc/lib/rcmain:24 *eval* pid N cycle C 10 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 12 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 13 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 15 Xword (b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 17 Xmark (a b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 18 Xword () (a b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 20 Xassign (x) (a b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 21 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 22 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 23 Xmark () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 24 Xword () () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 26 Xdol (x) () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 27 Xdol (a b) ()
      /rc/lib/rcmain:24 *eval*:1: $ variable name not singleton!
      """

  @FOG_RC_RUN_048
  Scenario: rc -r traces it as 9front rc does: Locals, subscripts and joined values
    When rc runs the script
      """
      rc -r -c 'fn f { y=($*(2-) $*(1)) z=$"* whatis y z }; f a b c'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('fn f { y=($*(2-) $*(1)) z=$"* whatis y z }; f a b c')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval 'fn f { y=($*(2-) $*(1)) z=$"* whatis y z }; f a b c')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 7 Xfn (f)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 54 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 55 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 57 Xword (c)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 59 Xword (b c)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 61 Xword (a b c)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 63 Xsimple (f a b c)
      /rc/lib/rcmain:24 *eval* pid N cycle C 10 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 12 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 13 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 14 Xword () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 16 Xmark ('*') ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 17 Xword () ('*') ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 19 F (1) ('*') ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 20 Xmark (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 21 Xword () (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 23 Xmark ('*') (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 24 Xword () ('*') (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 26 F (2-) ('*') (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 27 Xmark (b c a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 28 Xword () (b c a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 30 Xlocal (y) (b c a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 31 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 32 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 33 Xmark () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 34 Xword () () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 36 Xdol ('*') () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 37 Xqw (a b c) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 38 Xpush ('a b c') ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 39 Xmark ('a b c')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 40 Xword () ('a b c')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 42 Xlocal (z) ('a b c')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 43 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 44 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 46 Xword (z)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 48 Xword (y z)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 50 Xsimple (whatis y z)
      y=(b c a)
      z='a b c'
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 51 Xunlocal
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 52 Xunlocal
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 53 Xreturn
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 64 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      """

  @FOG_RC_RUN_049
  Scenario: rc -r traces it as 9front rc does: A glob is replaced by the names it matches
    When rc runs the script
      """
      rc -r -c 'whatis whatis >/tmp/q1; whatis whatis >/tmp/q2; for(i in /tmp/q*) whatis i; echo /tmp/q? /tmp/nomatch*'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('whatis whatis >/tmp/q1; whatis whatis >/tmp/q2; for(i in /tmp/q*) whatis i; echo /tmp/q? /tmp/nomatch*')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval 'whatis whatis >/tmp/q1; whatis whatis >/tmp/q2; for(i in /tmp/q*) whatis i; echo /tmp/q? /tmp/nomatch*')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 7 Xwrite (/tmp/q1)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 9 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 10 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 12 Xword (whatis)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 14 Xsimple (whatis whatis)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 15 Xpopredir
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 16 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 17 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 19 Xwrite (/tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 21 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 22 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 24 Xword (whatis)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 26 Xsimple (whatis whatis)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 27 Xpopredir
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 28 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 29 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 31 Xglob ('/tmp/q\u0001*')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 32 Xmark (/tmp/q1 /tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 33 Xmark () (/tmp/q1 /tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 34 Xword () () (/tmp/q1 /tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 36 Xlocal (i) () (/tmp/q1 /tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 37 Xfor (/tmp/q1 /tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 39 Xmark (/tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 40 Xword () (/tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 42 Xword (i) (/tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 44 Xsimple (whatis i) (/tmp/q2)
      i=/tmp/q1
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 45 Xjump (/tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 37 Xfor (/tmp/q2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 39 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 40 Xword () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 42 Xword (i) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 44 Xsimple (whatis i) ()
      i=/tmp/q2
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 45 Xjump ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 37 Xfor ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 47 Xunlocal
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 48 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 49 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 51 Xword ('/tmp/nomatch\u0001*')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 53 Xword ('/tmp/q\u0001?' '/tmp/nomatch\u0001*')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 55 Xglob (echo '/tmp/q\u0001?' '/tmp/nomatch\u0001*')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 56 Xsimple (echo /tmp/q1 /tmp/q2 '/tmp/nomatch*')
      /tmp/q1 /tmp/q2 /tmp/nomatch*
      """

  @FOG_RC_RUN_051
  Scenario: rc -r traces it as 9front rc does: sigexit runs when rc exits
    When rc runs the script
      """
      rc -r -c 'fn sigexit { whatis sigexit }; exit oops'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('fn sigexit { whatis sigexit }; exit oops')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval 'fn sigexit { whatis sigexit }; exit oops')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 7 Xfn (sigexit)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 19 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 20 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 22 Xword (oops)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 24 Xsimple (exit oops)
      /rc/lib/rcmain:24 *eval* pid N cycle C 10 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 12 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 13 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 15 Xword (sigexit)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 17 Xsimple (whatis sigexit)
      fn sigexit {
      	whatis sigexit
      }
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 18 Xreturn
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 24 Xsimple (exit oops)
      """

  @FOG_RC_RUN_052
  Scenario: rc -r traces it as 9front rc does: rc -e exits on a false status
    When rc runs the script
      """
      rc -r -e -c '~ a a; ~ a b; whatis notreached'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('~ a a; ~ a b; whatis notreached')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval '~ a a; ~ a b; whatis notreached')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 7 Xmark (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 8 Xword () (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 10 Xqw (a) (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 11 Xmatch (a) (a)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 12 Xeflag
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 13 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 14 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 16 Xmark (b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 17 Xword () (b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 19 Xqw (a) (b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 20 Xmatch (a) (b)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 21 Xeflag
      """

  @FOG_RC_RUN_053
  Scenario: A file redirection keeps its descriptor from a dup that would overwrite it
    When rc runs the script
      """
      {echo hi >/tmp/a} >[3=1]; cat /tmp/a
      """
    Then it prints
      """
      hi
      """

  @FOG_RC_RUN_054
  Scenario: . reads /dev/stdin from descriptor 0 where there is no such file
    When rc runs the script
      """
      . /dev/stdin; echo ok
      """
    Then it prints
      """
      ok
      """

  @FOG_RC_RUN_055
  Scenario: . takes -- as the end of its flags
    When rc runs the script
      """
      echo 'echo $*' >/tmp/d; . -- /tmp/d a b
      """
    Then it prints
      """
      a b
      """

  @FOG_RC_RUN_056
  Scenario: builtin runs a builtin even when a function has its name
    When rc runs the script
      """
      fn cd { echo not this }; builtin cd /tmp; echo x >f; cat /tmp/f; builtin
      """
    Then it prints
      """
      x
      /tmp/s:1: builtin: empty argument list
      """

  @FOG_RC_RUN_057
  Scenario: shift takes a signed count
    When rc runs the script
      """
      fn f { shift +1; echo $*; shift -1; echo $* }; f a b c
      """
    Then it prints
      """
      b c
      b c
      """

  @FOG_RC_RUN_058
  Scenario: rfork without flags, and with too many arguments
    When rc runs the script
      """
      rfork; echo $status ok; rfork a b; echo $status
      """
    Then it prints
      """
       ok
      Usage: rfork [fnesFNEm]
      rfork usage
      """

  @FOG_RC_RUN_059
  Scenario: rfork F leaves rc with no descriptors
    When rc runs the script
      """
      rc -c 'rfork F; exit done'; ~ $status *done && echo cleared
      """
    Then it prints
      """
      cleared
      """

  @FOG_RC_RUN_060
  Scenario: cd to a missing home
    When rc runs the script
      """
      home=/nonesuch; cd; echo $status
      """
    Then it prints
      """
      Can't cd /nonesuch: file does not exist: '/nonesuch'
      can't cd
      """

  @FOG_RC_RUN_061
  Scenario: Globs in the current directory, UTF-8 of three and four bytes, missing directories and broken classes
    When rc runs the script
      """
      mkdir /tmp/h; cd /tmp/h; echo >ab; echo >€.u; echo >😀.u; echo *; echo ?.u; echo /nonesuch/*; ~ a [a; echo $status; ~ a [a-; echo $status
      """
    Then it prints
      """
      ab €.u 😀.u
      €.u 😀.u
      /nonesuch/*
      no match
      no match
      """

  @FOG_RC_RUN_062
  Scenario: rc exits successfully when its status was cleared
    When rc runs the script
      """
      rc -c 'status=()'; echo $status ok
      """
    Then it prints
      """
       ok
      """

  @FOG_RC_RUN_063
  Scenario: >> creates a file that is missing
    When rc runs the script
      """
      echo a >>/tmp/new; cat /tmp/new; echo b >>/nonesuch/x
      """
    Then it prints
      """
      a
      /tmp/s:1: >> can't open: /nonesuch/x: file does not exist: '/nonesuch'
      """

  @FOG_RC_RUN_064
  Scenario: rc -x prints each simple command
    When rc runs the script
      """
      rc -x -c 'echo a'
      """
    Then it prints
      """
      . -bq /rc/lib/rcmain
      flag p
      finit
      . -bq '/env/fn#sigexit'
      flag l
      eval 'echo a'
      echo a
      a
      """

  @FOG_RC_RUN_065
  Scenario: rc -I is not interactive
    When rc runs the script
      """
      rc -I -c 'flag i || echo not interactive'
      """
    Then it prints
      """
      not interactive
      """

  @FOG_RC_RUN_066
  Scenario: Names starting with ./ or ../ are not looked up in $path
    When rc runs the script
      """
      ./nonesuch; ../nonesuch; .nonesuch
      """
    Then it prints
      """
      /tmp/s:1: ./nonesuch: file does not exist: './nonesuch'
      /tmp/s:1: ../nonesuch: file does not exist: '../nonesuch'
      /tmp/s:1: .nonesuch: file does not exist: './.nonesuch'
      """

  @FOG_RC_RUN_067
  Scenario: A here document joins a list's words with spaces
    When rc runs the script
      """
      x=(a b); cat <<EOF
      $x
      EOF
      """
    Then it prints
      """
      a b
      """

  @FOG_RC_RUN_068
  Scenario: A case may hold any command, and the last case may stand alone
    When rc runs the script
      """
      switch(abc){
      case a*
      	if(~ 1 1) echo inside
      case b
      	echo b
      }
      switch(z){
      case a
      	echo a
      case z
      	echo last
      }
      switch(q){
      case a
      	echo a
      case q
      }
      switch(a){case a}
      echo not reached
      """
    Then it prints
      """
      inside
      last
      /tmp/s:19: case missing in switch
      """

  @FOG_RC_RUN_069
  Scenario: A local's end puts the variable back in the environment
    When rc runs the script
      """
      x=g; x=l rc -c 'echo $x'; rc -c 'echo $x'
      """
    Then it prints
      """
      l
      g
      """

  @FOG_RC_RUN_070
  Scenario: <{} names the lowest free descriptor
    When rc runs the script
      """
      echo <{echo} >/tmp/fd1; {echo <{echo}} >[3]/dev/null; cat /tmp/fd1; echo <{echo} <{echo}
      """
    Then it prints
      """
      /fd/6
      /fd/5
      /fd/7 /fd/4
      """

  @FOG_RC_RUN_071
  Scenario: A command over two lines has one source line
    When rc runs the script
      """
      rc -r -c 'if(
      ~ a a) whatis x'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('if(
      ~ a a) whatis x')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval 'if(
      ~ a a) whatis x')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 7 Xmark (a)
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 8 Xword () (a)
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 10 Xqw (a) (a)
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 11 Xmatch (a) (a)
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 12 Xif
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 14 Xmark
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 15 Xword ()
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 17 Xword (x)
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 19 Xsimple (whatis x)
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 20 Xwastrue
      /rc/lib/rcmain:24 *eval*:2 pid N cycle C 21 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      """

  @FOG_RC_RUN_072
  Scenario: An interactive rc reports errors and goes on
    When rc -i reads the script
      """
      echo ()^x
      echo (a b)^(1 2 3)
      x=(a b); echo $$x
      echo $#$x
      $x=1
      $x=1 echo
      exec
      . -z
      . /nonesuch
      flag a b c
      wait a b
      eval
      whatis
      builtin
      y=(); $y
      prompt=(pp)
      echo after prompt
      prompt=()
      echo default prompt
      """
    Then it prints, ending with a prompt
      """
      % /fd/0:1: null list in concatenation
      % /fd/0:2: mismatched list lengths in concatenation
      % /fd/0:3: $ variable name not singleton!
      % /fd/0:4: $# variable name not singleton!
      % /fd/0:5: = variable name not singleton!
      % /fd/0:6: local variable name must be singleton
      % /fd/0:7: exec: empty argument list
      % /fd/0:8: Usage: . [-biq] file [arg ...]
      % /fd/0:9: . can't open: /nonesuch: file does not exist: '/nonesuch'
      % /fd/0:10: Usage: flag [letter] [+-]
      % /fd/0:11: Usage: wait [pid]
      % /fd/0:12: Usage: eval cmd ...
      % /fd/0:13: Usage: whatis name ...
      % /fd/0:14: builtin: empty argument list
      % /fd/0:15: empty argument list
      % ppafter prompt
      pp% default prompt
      % 
      """

  @FOG_RC_RUN_073
  Scenario: if not inside a block does not follow the if before it
    When rc runs the script
      """
      if(~ a a) echo a; {if not echo b}
      echo next
      """
    Then it prints
      """
      /tmp/s:2: `if not' does not follow `if(...)'
      """

  @FOG_RC_RUN_074
  Scenario: Glob characters in names that are not patterns are plain
    When rc runs the script
      """
      fn zz* { echo star }; zz*; 'x*'=1; echo $(x^*); y*=2; echo $'y*'; z*=3 whatis 'z*'; fn f { echo $"* $*(2) }; f a b
      """
    Then it prints
      """
      star
      1
      2
      z*=3
      a b b
      """

  @FOG_RC_RUN_075
  Scenario: A variable's name is cut to fit rc's 128-byte environment path
    When rc runs the script
      """
      a=aaaaaaaaaa; n=$a^$a^$a^$a^$a^$a^$a^$a^$a^$a^$a^$a^$a; m=$a^$a^$a^$a^$a^$a^$a^$a^$a^$a^$a^$a^aa; $n=v; rc -c 'whatis '^$m
      """
    Then it prints
      """
      aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=v
      """

  @FOG_RC_RUN_076
  Scenario: A here document needs a /tmp where files can be made
    When rc runs the script
      """
      bind /dev /tmp
      cat <<EOF
      x
      EOF
      """
    Then it prints
      """
      /tmp/s:2: << can't get temp file: /tmp/hereN: mounted directory forbids creation: '/tmp/hereN'
      """

  @FOG_RC_RUN_077
  Scenario: bind reports what it cannot bind
    When rc runs the script
      """
      echo >/tmp/f
      bind
      bind -ab /tmp /tmp
      bind /nonesuch /tmp
      bind /tmp /nonesuch
      bind /tmp/f /dev
      bind -q /nonesuch /tmp; echo $status
      bind -z a b
      mkdir /tmp/b1 /tmp/b2; echo >/tmp/b2/x; bind -bc /tmp/b2 /tmp/b1; cat /tmp/b1/x && echo bound
      """
    Then it prints
      """
      usage: bind [-b|-a|-c|-bc|-ac] new old
      usage: bind [-b|-a|-c|-bc|-ac] new old
      bind: /nonesuch: file does not exist: '/nonesuch'
      bind: /nonesuch: file does not exist: '/nonesuch'
      bind /tmp/f /dev: inconsistent mount

      usage: bind [-b|-a|-c|-bc|-ac] new old

      bound
      """

  @FOG_RC_RUN_090
  Scenario: rc reports a pipe it cannot make
    Given rc starts with all but two of its 5000 descriptors in use
    When rc runs the script
      """
      x=`{echo a}
      """
    Then it prints
      """
      /tmp/s:1: can't make pipe: no free file descriptors
      """

  @FOG_RC_RUN_091
  Scenario: eval, shift, flag, cd, wait, whatis, rfork and . pop their arguments
    When rc runs the script
      """
      rc -r -c 'eval ''x=1''; shift; flag z +; flag z -; cd /tmp; wait; whatis cd; rfork e; echo ''y=2'' >/tmp/dotf; . /tmp/dotf; echo $x $y'
      """
    Then it traces
      """
      *bootstrap* pid N cycle C 2 Xmark ()
      *bootstrap* pid N cycle C 3 Xword () ()
      *bootstrap* pid N cycle C 5 Xassign ('*') ()
      *bootstrap* pid N cycle C 6 Xmark
      *bootstrap* pid N cycle C 7 Xmark ()
      *bootstrap* pid N cycle C 8 Xword () ()
      *bootstrap* pid N cycle C 10 Xdol ('*') ()
      *bootstrap* pid N cycle C 11 Xword ()
      *bootstrap* pid N cycle C 13 Xword (/rc/lib/rcmain)
      *bootstrap* pid N cycle C 15 Xword (-bq /rc/lib/rcmain)
      *bootstrap* pid N cycle C 17 Xsimple (. -bq /rc/lib/rcmain)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:2 pid N cycle C 4 Xmark
      /rc/lib/rcmain:2 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:2 pid N cycle C 7 Xmark (0)
      /rc/lib/rcmain:2 pid N cycle C 8 Xmark () (0)
      /rc/lib/rcmain:2 pid N cycle C 9 Xword () () (0)
      /rc/lib/rcmain:2 pid N cycle C 11 Xcount (home) () (0)
      /rc/lib/rcmain:2 pid N cycle C 12 Xqw (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 13 Xmatch (1) (0)
      /rc/lib/rcmain:2 pid N cycle C 14 Xif
      /rc/lib/rcmain:2 pid N cycle C 24 Xsrcline
      /rc/lib/rcmain:3 pid N cycle C 26 Xmark
      /rc/lib/rcmain:3 pid N cycle C 27 Xword ()
      /rc/lib/rcmain:3 pid N cycle C 29 Xmark (0)
      /rc/lib/rcmain:3 pid N cycle C 30 Xmark () (0)
      /rc/lib/rcmain:3 pid N cycle C 31 Xword () () (0)
      /rc/lib/rcmain:3 pid N cycle C 33 Xcount (ifs) () (0)
      /rc/lib/rcmain:3 pid N cycle C 34 Xqw (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 35 Xmatch (1) (0)
      /rc/lib/rcmain:3 pid N cycle C 36 Xif
      /rc/lib/rcmain:3 pid N cycle C 50 Xsrcline
      /rc/lib/rcmain:5 pid N cycle C 52 Xmark
      /rc/lib/rcmain:5 pid N cycle C 53 Xmark ()
      /rc/lib/rcmain:5 pid N cycle C 54 Xword () ()
      /rc/lib/rcmain:5 pid N cycle C 56 Xcount (prompt) ()
      /rc/lib/rcmain:5 pid N cycle C 57 Xqw (2)
      /rc/lib/rcmain:5 pid N cycle C 58 Xjump (2)
      /rc/lib/rcmain:5 pid N cycle C 62 Xmark (2)
      /rc/lib/rcmain:5 pid N cycle C 63 Xsrcline () (2)
      /rc/lib/rcmain:6 pid N cycle C 65 Xword () (2)
      /rc/lib/rcmain:6 pid N cycle C 67 Xcase (0) (2)
      /rc/lib/rcmain:6 pid N cycle C 82 Xmark (2)
      /rc/lib/rcmain:6 pid N cycle C 83 Xsrcline () (2)
      /rc/lib/rcmain:8 pid N cycle C 85 Xword () (2)
      /rc/lib/rcmain:8 pid N cycle C 87 Xcase (1) (2)
      /rc/lib/rcmain:8 pid N cycle C 104 Xpopm (2)
      /rc/lib/rcmain:8 pid N cycle C 105 Xsrcline
      /rc/lib/rcmain:11 pid N cycle C 107 Xmark
      /rc/lib/rcmain:11 pid N cycle C 108 Xword ()
      /rc/lib/rcmain:11 pid N cycle C 110 Xmark ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 111 Xmark () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 112 Xword () () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 114 Xdol (rcname) () ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 115 Xqw (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 116 Xmatch (rc) ('\u0001?.out')
      /rc/lib/rcmain:11 pid N cycle C 117 Xif
      /rc/lib/rcmain:11 pid N cycle C 129 Xsrcline
      /rc/lib/rcmain:12 pid N cycle C 131 Xmark
      /rc/lib/rcmain:12 pid N cycle C 132 Xword ()
      /rc/lib/rcmain:12 pid N cycle C 134 Xword (p)
      /rc/lib/rcmain:12 pid N cycle C 136 Xsimple (flag p)
      /rc/lib/rcmain:12 pid N cycle C 137 Xif
      /rc/lib/rcmain:12 pid N cycle C 147 Xsrcline
      /rc/lib/rcmain:13 pid N cycle C 149 Xifnot
      /rc/lib/rcmain:13 pid N cycle C 151 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 153 Xmark
      /rc/lib/rcmain:14 pid N cycle C 154 Xword ()
      /rc/lib/rcmain:14 pid N cycle C 156 Xsimple (finit)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:14 pid N cycle C 4 Xmark
      /rc/lib/rcmain:14 pid N cycle C 5 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 6 Xword () ()
      /rc/lib/rcmain:14 pid N cycle C 8 Xmark ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 9 Xword () ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 11 Xconc ('/env/fn#') ('\u0001*') ()
      /rc/lib/rcmain:14 pid N cycle C 12 Xglob ('/env/fn#\u0001*')
      /rc/lib/rcmain:14 pid N cycle C 13 Xmark ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 14 Xmark () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 15 Xword () () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 17 Xlocal (i) () ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ('/env/fn#sigexit')
      /rc/lib/rcmain:14 pid N cycle C 20 Xmark ()
      /rc/lib/rcmain:14 pid N cycle C 21 Xmark () ()
      /rc/lib/rcmain:14 pid N cycle C 22 Xword () () ()
      /rc/lib/rcmain:14 pid N cycle C 24 Xdol (i) () ()
      /rc/lib/rcmain:14 pid N cycle C 25 Xword ('/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 27 Xword (-bq '/env/fn#sigexit') ()
      /rc/lib/rcmain:14 pid N cycle C 29 Xsimple (. -bq '/env/fn#sigexit') ()
      *rdcmds* pid N cycle C 2 Xrdcmds
      /env/fn#sigexit pid N cycle C 2 Xsrcline
      /env/fn#sigexit:1 pid N cycle C 4 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 30 Xjump ()
      /rc/lib/rcmain:14 pid N cycle C 18 Xfor ()
      /rc/lib/rcmain:14 pid N cycle C 32 Xunlocal
      /rc/lib/rcmain:14 pid N cycle C 33 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:14 pid N cycle C 157 Xsrcline
      /rc/lib/rcmain:15 pid N cycle C 159 Xmark
      /rc/lib/rcmain:15 pid N cycle C 160 Xword ()
      /rc/lib/rcmain:15 pid N cycle C 162 Xmark (0)
      /rc/lib/rcmain:15 pid N cycle C 163 Xmark () (0)
      /rc/lib/rcmain:15 pid N cycle C 164 Xword () () (0)
      /rc/lib/rcmain:15 pid N cycle C 166 Xcount (path) () (0)
      /rc/lib/rcmain:15 pid N cycle C 167 Xqw (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 168 Xmatch (2) (0)
      /rc/lib/rcmain:15 pid N cycle C 169 Xif
      /rc/lib/rcmain:15 pid N cycle C 181 Xsrcline
      /rc/lib/rcmain:17 pid N cycle C 183 Xmark
      /rc/lib/rcmain:17 pid N cycle C 184 Xword ()
      /rc/lib/rcmain:17 pid N cycle C 186 Xdelfn (sigexit)
      /rc/lib/rcmain:17 pid N cycle C 187 Xsrcline
      /rc/lib/rcmain:18 pid N cycle C 189 Xmark
      /rc/lib/rcmain:18 pid N cycle C 190 Xword ()
      /rc/lib/rcmain:18 pid N cycle C 192 Xmark (0)
      /rc/lib/rcmain:18 pid N cycle C 193 Xmark () (0)
      /rc/lib/rcmain:18 pid N cycle C 194 Xword () () (0)
      /rc/lib/rcmain:18 pid N cycle C 196 Xcount (cflag) () (0)
      /rc/lib/rcmain:18 pid N cycle C 197 Xqw (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 198 Xmatch (1) (0)
      /rc/lib/rcmain:18 pid N cycle C 199 Xbang
      /rc/lib/rcmain:18 pid N cycle C 200 Xif
      /rc/lib/rcmain:18 pid N cycle C 202 Xsrcline
      /rc/lib/rcmain:19 pid N cycle C 204 Xmark
      /rc/lib/rcmain:19 pid N cycle C 205 Xword ()
      /rc/lib/rcmain:19 pid N cycle C 207 Xword (l)
      /rc/lib/rcmain:19 pid N cycle C 209 Xsimple (flag l)
      /rc/lib/rcmain:19 pid N cycle C 210 Xif
      /rc/lib/rcmain:19 pid N cycle C 265 Xsrcline
      /rc/lib/rcmain:23 pid N cycle C 267 Xmark
      /rc/lib/rcmain:23 pid N cycle C 268 Xword ()
      /rc/lib/rcmain:23 pid N cycle C 270 Xmark ('')
      /rc/lib/rcmain:23 pid N cycle C 271 Xword () ('')
      /rc/lib/rcmain:23 pid N cycle C 273 Xassign (status) ('')
      /rc/lib/rcmain:23 pid N cycle C 274 Xsrcline
      /rc/lib/rcmain:24 pid N cycle C 276 Xmark
      /rc/lib/rcmain:24 pid N cycle C 277 Xmark ()
      /rc/lib/rcmain:24 pid N cycle C 278 Xword () ()
      /rc/lib/rcmain:24 pid N cycle C 280 Xdol (cflag) ()
      /rc/lib/rcmain:24 pid N cycle C 281 Xword ('eval ''x=1''; shift; flag z +; flag z -; cd /tmp; wait; whatis cd; rfork e; echo ''y=2'' >/tmp/dotf; . /tmp/dotf; echo $x $y')
      /rc/lib/rcmain:24 pid N cycle C 283 Xsimple (eval 'eval ''x=1''; shift; flag z +; flag z -; cd /tmp; wait; whatis cd; rfork e; echo ''y=2'' >/tmp/dotf; . /tmp/dotf; echo $x $y')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 7 Xword ('x=1')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 9 Xsimple (eval 'x=1')
      *rdcmds* pid N cycle C 2 Xrdcmds
      /rc/lib/rcmain:24 *eval*:1 *eval* pid N cycle C 2 Xsrcline
      /rc/lib/rcmain:24 *eval*:1 *eval*:1 pid N cycle C 4 Xmark
      /rc/lib/rcmain:24 *eval*:1 *eval*:1 pid N cycle C 5 Xword ()
      /rc/lib/rcmain:24 *eval*:1 *eval*:1 pid N cycle C 7 Xmark (1)
      /rc/lib/rcmain:24 *eval*:1 *eval*:1 pid N cycle C 8 Xword () (1)
      /rc/lib/rcmain:24 *eval*:1 *eval*:1 pid N cycle C 10 Xassign (x) (1)
      /rc/lib/rcmain:24 *eval*:1 *eval*:1 pid N cycle C 11 Xreturn
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 10 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 11 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 13 Xsimple (shift)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 14 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 15 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 17 Xword (+)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 19 Xword (z +)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 21 Xsimple (flag z +)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 22 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 23 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 25 Xword (-)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 27 Xword (z -)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 29 Xsimple (flag z -)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 30 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 31 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 33 Xword (/tmp)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 35 Xsimple (cd /tmp)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 36 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 37 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 39 Xsimple (wait)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 40 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 41 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 43 Xword (cd)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 45 Xsimple (whatis cd)
      builtin cd
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 46 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 47 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 49 Xword (e)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 51 Xsimple (rfork e)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 52 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 53 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 55 Xwrite (/tmp/dotf)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 57 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 58 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 60 Xword ('y=2')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 62 Xsimple (echo 'y=2')
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 63 Xpopredir
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 64 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 65 Xword ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 67 Xword (/tmp/dotf)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 69 Xsimple (. /tmp/dotf)
      *rdcmds* pid N cycle C 2 Xrdcmds
      /tmp/dotf pid N cycle C 2 Xsrcline
      /tmp/dotf:1 pid N cycle C 4 Xmark
      /tmp/dotf:1 pid N cycle C 5 Xword ()
      /tmp/dotf:1 pid N cycle C 7 Xmark (2)
      /tmp/dotf:1 pid N cycle C 8 Xword () (2)
      /tmp/dotf:1 pid N cycle C 10 Xassign (y) (2)
      /tmp/dotf:1 pid N cycle C 11 Xreturn
      *rdcmds* pid N cycle C 2 Xrdcmds
      *rdcmds* pid N cycle C 3 Xreturn
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 70 Xmark
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 71 Xmark ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 72 Xword () ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 74 Xdol (y) ()
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 75 Xmark (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 76 Xword () (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 78 Xdol (x) (2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 79 Xword (1 2)
      /rc/lib/rcmain:24 *eval*:1 pid N cycle C 81 Xsimple (echo 1 2)
      1 2
      """

  @FOG_RC_RUN_092
  Scenario: Builtins that succeed clear the status
    When rc runs the script
      """
      ~ a b; cd /tmp; echo $status
      ~ a b; shift; echo $status
      ~ a b; flag z +; echo $status
      ~ a b; flag z; echo $status
      ~ a b; rfork e; echo $status
      ~ a b; whatis cd; echo $status
      ~ a b; eval ''; echo $status
      whatis nonesuch; echo $status
      x=1; whatis x; echo $status
      """
    Then it prints
      """





      builtin cd

      no match
      not found
      x=1

      """

  @FOG_RC_RUN_093
  Scenario: shift takes a count of more than one digit
    When rc runs the script
      """
      fn f { shift 10; echo $* }; f 1 2 3 4 5 6 7 8 9 10 11
      """
    Then it prints
      """
      11
      """

  @FOG_RC_RUN_094
  Scenario: An exec'd command gets each of its redirections, closing what they opened
    When rc runs the script
      """
      echo one >/tmp/r1 >[2]/tmp/r2; echo two >[2]/tmp/r3 >/tmp/r4; cat /tmp/r1 /tmp/r4
      {echo x >[1=2]} >[2]/tmp/e; cat /tmp/e
      rc -c 'echo <{echo}' >/tmp/o; cat /tmp/o
      """
    Then it prints
      """
      one
      two
      x
      /fd/3
      """

  @FOG_RC_RUN_095
  Scenario: cd looks up names in $cdpath unless they start with ./ or ../, and prints where it went
    When rc runs the script
      """
      mkdir /tmp/c /tmp/c/.d /tmp/c/..d /tmp/c/x
      cdpath=/tmp/c
      cd /; cd .d
      cd /; cd ..d
      cd /; cd ./x
      cd /; cd ../x
      cd /; cd ...
      cdpath=('' /tmp)
      cd /; cd tmp
      """
    Then it prints
      """
      /tmp/c/.d
      /tmp/c/..d
      Can't cd ./x: file does not exist: './x'
      Can't cd ../x: file does not exist: '../x'
      Can't cd ...: file does not exist: '/tmp/c/...'
      """

  @FOG_RC_RUN_096
  Scenario: eval's text ends with a newline, so a comment ends with it
    When rc runs the script
      """
      eval 'echo a #comment'
      echo b
      """
    Then it prints
      """
      a
      b
      """

  @FOG_RC_RUN_097
  Scenario: . takes - alone as no flags
    When rc runs the script
      """
      echo 'echo dashed' >/tmp/d; . - /tmp/d
      """
    Then it prints
      """
      dashed
      """

  @FOG_RC_RUN_098
  Scenario: . may open its file on descriptor 0
    When rc runs the script
      """
      echo 'echo dotted' >/tmp/d; rc -c '. /tmp/d' <[0=]
      """
    Then it prints
      """
      dotted
      """

  @FOG_RC_RUN_099
  Scenario: . closes its file when it has read it
    When rc runs the script
      """
      echo >/tmp/empty; . /tmp/empty; echo <{echo}
      """
    Then it prints
      """
      /fd/4
      """

  @FOG_RC_RUN_100
  Scenario: . sets $0 and exports it
    When rc runs the script
      """
      echo 'rc -c ''echo $0''' >/tmp/d; . /tmp/d a b
      """
    Then it prints
      """
      /rc/lib/rcmain
      """

  @FOG_RC_RUN_101
  Scenario: flag with an empty letter or a long one
    When rc runs the script
      """
      flag ''; echo $status; flag ab +; echo $status
      """
    Then it prints
      """
      flag not set
      /tmp/s:1: Usage: flag [letter] [+-]
      """

  @FOG_RC_RUN_102
  Scenario: rfork without flags, or with e twice, gives rc its own environment
    When rc runs the script
      """
      x=old; rc -c 'rfork; x=new'; rc -c 'echo $x'
      rc -c 'rfork ee; x=new'; rc -c 'echo $x'
      """
    Then it prints
      """
      old
      old
      """

  @FOG_RC_RUN_103
  Scenario: An interactive rc reports . without a file
    When rc -i reads the script
      """
      .
      echo after
      """
    Then it prints, ending with a prompt
      """
      % /fd/0:1: Usage: . [-biq] file [arg ...]
      % after
      % 
      """

  @FOG_RC_RUN_104
  Scenario: Character classes compare code points of two, three and four bytes
    When rc runs the script
      """
      for(c in € ₭ ₟){ ~ $c [₠-€] && echo $c in }
      for(c in 😀 😂 😃){ ~ $c [😀-😂] && echo $c in }
      for(c in α ω ά){ ~ $c [ω-α] && echo $c in }
      ~ b [a-za-z] && echo twice in
      ~ '[' [ab] || echo bracket out
      ~ ã é || echo same lead byte
      ~ é é && echo same character
      ~ x€y x?y && echo one character
      """
    Then it prints
      """
      € in
      😀 in
      😂 in
      α in
      ω in
      twice in
      bracket out
      same lead byte
      same character
      one character
      """

  @FOG_RC_RUN_105
  Scenario: rc's deglob drops a literal GLOB byte from a word it globs, so the word no longer matches itself
    When rc runs the script, its \x escapes bytes
      """
      ~ \x01 \x01 || echo pair dropped
      ~ a\x01b a\x01b || echo inside dropped
      ~ a\x01b ab && echo leaving the bytes around it
      x=(/nonesuch/a\x01*b)
      ~ $x '/nonesuch/a'^\x01^'*b' && echo a later one kept
      """
    Then it prints
      """
      pair dropped
      inside dropped
      leaving the bytes around it
      a later one kept
      """

  @FOG_RC_RUN_106
  Scenario: Broken UTF-8 counts as single bytes and matches no class but its own
    When rc runs the script, its \x escapes bytes
      """
      ~ \xC3 ? && echo lone lead byte
      ~ \xC3\xA9\xA9 ?? && echo stray continuation
      ~ \xE2\x82 ? && echo cut three-byte sequence
      ~ \xF0\x9F\x98 ? && echo cut four-byte sequence
      ~ \xC3\xA9\xA9\xA9 ? || echo two characters
      ~ \xC3 [\xC3] && echo broken class member
      ~ \xC3 [\xC3-\xC3] && echo broken range
      ~ \xE2\x82 [\xE2\x82] && echo broken three-byte member
      ~ \xF0\x9F\x98 [\xF0\x9F\x98] && echo broken four-byte member
      """
    Then it prints
      """
      lone lead byte
      stray continuation
      cut three-byte sequence
      cut four-byte sequence
      two characters
      broken class member
      broken range
      broken three-byte member
      broken four-byte member
      """

  @FOG_RC_RUN_107
  Scenario: && skips its command when the status is false
    When rc runs the script
      """
      ~ a b && echo not this
      echo after
      """
    Then it prints
      """
      after
      """

  @FOG_RC_RUN_108
  Scenario: A command in the background shares rc's environment and ends where its code ends
    When rc runs the script
      """
      { y=fromjob; rc -c '' } &
      wait
      rc -c 'echo $y'
      echo once
      """
    Then it prints
      """
      fromjob
      once
      """

  @FOG_RC_RUN_109
  Scenario: A backquote's split may be a glob or a list, and its commands ignore -e
    When rc runs the script
      """
      x=`*{echo a*b*c}; echo $#x
      x=`(: ';'){echo 'a:b;c'}; echo $#x
      rc -e -c 'x=`{~ a b; echo y}; echo $x'
      """
    Then it prints
      """
      3
      3
      y
      """

  @FOG_RC_RUN_110
  Scenario: rc -e stops at a failing subshell, and while() loops after a failed status
    When rc runs the script
      """
      rc -e -c '@{~ a b}; echo not reached'
      ~ a b; @{ while() { echo looped; exit } }
      """
    Then it prints
      """
      looped
      """

  @FOG_RC_RUN_111
  Scenario: A for loop's variable name and a subscript are not globs
    When rc runs the script
      """
      for(i* in a) echo $'i*'
      cd /tmp; echo >1; a=(x y); echo $a(?)
      """
    Then it prints
      """
      a

      """

  @FOG_RC_RUN_112
  Scenario: <{} runs only its own command
    When rc runs the script
      """
      cat <{echo a}; echo after
      """
    Then it prints
      """
      a
      after
      """

  @FOG_RC_RUN_113
  Scenario: After a command and then an if on one line, the next line's if not does not follow it
    When rc runs the script
      """
      echo a; if(~ a b) echo b
      if not echo c
      """
    Then it prints
      """
      a
      /tmp/s:3: `if not' does not follow `if(...)'
      """

  @FOG_RC_RUN_114
  Scenario: Commands forked for &, @, a backquote or <{} end with their own code
    When rc runs the script
      """
      echo bg >/tmp/bg & echo fg; wait; cat /tmp/bg
      @{echo in}; echo after
      x=`{echo a}; echo $status st
      cat <{echo a}; wait; echo $status st
      """
    Then it prints
      """
      fg
      bg
      in
      after
       st
      a
       st
      """

  @FOG_RC_RUN_115
  Scenario: || skips its command when the status is true
    When rc runs the script
      """
      ~ a a || echo not this; echo after
      """
    Then it prints
      """
      after
      """

  @FOG_RC_RUN_116
  Scenario: rc -e stops at a failing simple command
    When rc runs the script
      """
      rc -e -c 'cat /nonesuch; echo not reached'
      """
    Then it prints
      """
      cat: can't open /nonesuch: file does not exist: '/nonesuch'
      """

  @FOG_RC_RUN_117
  Scenario: Names that would glob to files in the directory are not globbed
    When rc runs the script
      """
      mkdir /tmp/zglob; cd /tmp/zglob; echo >q; echo >zzfile; echo >ifile; echo >afile; echo >bfile; echo >cfile
      x=`*{echo q*y}; echo $x(1)
      fn zz* {echo star}; 'zz*'
      for(i* in a) echo $'i*'
      a*=1; echo $'a*'
      b*=2 whatis 'b*'
      switch(cx){case c*; echo matched}
      """
    Then it prints
      """
      q
      star
      a
      1
      b*=2
      matched
      """

  @FOG_RC_RUN_118
  Scenario: A switch whose body starts with a command that is not a case is missing its case
    When rc runs the script
      """
      switch(a){echo x; case a}
      echo not reached
      """
    Then it prints
      """
      /tmp/s:2: case missing in switch
      """

  @FOG_RC_RUN_119
  Scenario: The descriptors rc holds while it runs a script
    When rc runs the script
      """
      echo /fd/*
      x=`{echo /fd/*}; echo $x
      rc -c 'echo /fd/*'
      """
    Then it prints
      """
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl /fd/4 /fd/4ctl
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl /fd/4 /fd/4ctl /fd/5 /fd/5ctl
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl
      """

  @FOG_RC_RUN_120
  Scenario: Pipes and files are not left open in the commands rc starts
    When rc runs the script
      """
      rc -c 'echo /fd/*' | cat
      x=`{rc -c 'echo /fd/*'}; echo $x
      cat <{rc -c 'echo /fd/*'}
      rc -c 'echo /fd/*' >/tmp/fo; cat /tmp/fo
      cat <<EOF >/dev/null; rc -c 'echo /fd/*'
      x
      EOF
      x=1; rc -c 'rc -c ''echo /fd/*'''
      """
    Then it prints
      """
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl
      """

  @FOG_RC_RUN_121
  Scenario: A file opened on a descriptor a dup will clobber is moved off it
    When rc runs the script
      """
      echo hi >[5=1] >/tmp/cl1; cat /tmp/cl1
      echo ho >/tmp/cl2 >[5=1]; cat /tmp/cl2
      echo hu >[5=1] >[6=1] >[7=1] >/tmp/cl3; cat /tmp/cl3
      """
    Then it prints
      """
      hi
      ho
      hu
      """

  @FOG_RC_RUN_122
  Scenario: A return in a subshell goes on in the caller, with its locals and redirections
    When rc runs the script
      """
      fn f { @{ return } }
      x=1 { f; echo x is $x } >/tmp/sr
      cat /tmp/sr
      """
    Then it prints
      """
      /tmp/s:1: return: file does not exist: './return'
      x is 1
      """

  @FOG_RC_RUN_123
  Scenario: A changed status is written to /env for the commands rc runs
    When rc runs the script
      """
      false
      s=`{cat /env/status}; ~ $s *false && echo false written
      true
      s=`{cat /env/status}; echo $#s
      """
    Then it prints
      """
      /tmp/s:1: false: file does not exist: './false'
      /tmp/s:3: true: file does not exist: './true'
      7
      """

  @FOG_RC_RUN_124
  Scenario: The arguments of a function, a dot file and shift are written to /env
    When rc runs the script
      """
      fn f { a=`{cat '/env/*'}; echo $a }
      f p q
      fn g { shift; a=`{cat '/env/*'}; echo $a }
      g p q r
      echo 'a=`{cat ''/env/*''}; echo $a; b=`{cat /env/0}; echo $b' >/tmp/dstar
      . /tmp/dstar u v
      """
    Then it prints
      """
      p q
      q r
      u v
      /tmp/dstar
      """

  @FOG_RC_RUN_125
  Scenario: A for loop's variable is written to /env each time round
    When rc runs the script
      """
      for(i in a b) rc -c 'echo $i'
      """
    Then it prints
      """
      a
      b
      """

  @FOG_RC_RUN_126
  Scenario: A variable or function another rc rewrote in the shared /env is not written back unless it changed
    When rc runs the script
      """
      x=1; cat /dev/null; rc -c 'x=2; cat /dev/null'; cat /dev/null; y=`{cat /env/x}; echo $y
      fn g {echo 1}; cat /dev/null; rc -c 'fn g {echo 2}; cat /dev/null'; cat /dev/null; rc -c g
      z=1; rc -c 'rc -c ''z=3; cat /dev/null''; cat /dev/null; y=`{cat /env/z}; echo $y'
      """
    Then it prints
      """
      2
      2
      3
      """

  @FOG_RC_RUN_127
  Scenario: A function is written to /env as its definition
    When rc runs the script
      """
      fn f {echo hi}; cat /dev/null; cat '/env/fn#f'
      """
    Then it prints
      """
      fn f {
      	echo hi
      }
      """

  @FOG_RC_RUN_128
  Scenario: An empty file and a function's file in /env are not read as variables
    When rc runs the script
      """
      echo -n >/env/zz; rc -c 'whatis zz; echo $#zz'
      fn ff {echo}; cat /dev/null; rc -c 'whatis ''fn#ff''; echo $status'
      """
    Then it prints
      """
      0
      not found
      """

  @FOG_RC_RUN_130
  Scenario: A here document tries 26 names for its file
    When rc runs the script
      """
      x=(0 1 2 3 4 5 6 7 8 9)
      for(i in $x) cat /dev/null
      rc -c 'switch($pid){
      case ?
      	r=00000000
      case ??
      	r=0000000
      case ???
      	r=000000
      case ????
      	r=00000
      case ?????
      	r=0000
      }
      for(c in 0 a b c d e f g h i j k l m n o p q r s t u v w x) echo >/tmp/here^$c^$r^$pid^AA
      cat <<EOF
      x
      EOF
      echo $status
      cat <<EOF
      y
      EOF
      '
      """
    Then it prints
      """
      /rc/lib/rcmain:24 *eval*:14: << can't get temp file: /tmp/hereN: 
      """

  @FOG_RC_RUN_131
  Scenario: Output longer than rc's buffer is written in pieces
    When rc runs the script
      """
      x=0123456789; x=$x^$x^$x^$x^$x^$x^$x^$x^$x^$x; x=$x^$x^$x^$x^$x^$x^$x^$x^$x^$x; x=$x^$x^$x^$x^$x^$x^$x^$x^$x^$x; x=$x^$x^$x^$x^$x
      whatis x >/tmp/big; y=`{cat /tmp/big}; echo $#y
      """
    Then it prints
      """
      1
      """

  @FOG_RC_RUN_132
  Scenario: A dup redirection takes effect for the command
    When rc runs the script
      """
      cat /nonesuch >/tmp/rd1 >[2=1]; echo one; cat /tmp/rd1
      cat /nonesuch >[2=1] >/tmp/rd2; echo two; cat /tmp/rd2
      """
    Then it prints
      """
      one
      cat: can't open /nonesuch: file does not exist: '/nonesuch'
      cat: can't open /nonesuch: file does not exist: '/nonesuch'
      two
      """

  @FOG_RC_RUN_133
  Scenario: whatis writes nothing on a closed descriptor
    When rc runs the script
      """
      x=1; whatis x >[1=]; echo after
      """
    Then it prints
      """
      after
      """

  @FOG_RC_RUN_134
  Scenario: A pipeline's left side writes into the pipe, and a reader that leaves early stops it
    When rc runs the script
      """
      echo a | {x=`{cat}; echo $#x}
      cat /dev/zero | echo x
      """
    Then it prints
      """
      1
      x
      """

  @FOG_RC_RUN_135
  Scenario: A backquote's command is waited for
    When rc runs the script
      """
      false; x=`{echo a}; ~ $status '' && echo collected
      """
    Then it prints
      """
      /tmp/s:1: false: file does not exist: './false'
      collected
      """

  @FOG_RC_RUN_136
  Scenario: wait waits for every child, or for the one named
    When rc runs the script
      """
      x=(0 1 2 3 4 5 6 7 8 9)
      {for(i in $x) for(j in $x) cat /dev/null; echo bg >/tmp/wb} &
      wait; cat /tmp/wb
      {for(i in $x) for(j in $x) cat /dev/null; echo bg2 >/tmp/wb2} &
      wait $apid; cat /tmp/wb2
      {for(i in $x) for(j in $x) cat /dev/null; exit slow} &
      s=$apid; true & f=$apid; wait $f; wait $s; ~ $status *slow && echo slow
      {for(i in $x) for(j in $x) cat /dev/null; exit slow2} &
      s=$apid; wait 0; wait $s; ~ $status *slow2 && echo slow2
      """
    Then it prints
      """
      bg
      bg2
      /tmp/s:7: true: file does not exist: './true'
      slow
      slow2
      """

  @FOG_RC_RUN_137
  Scenario: rc exits with an empty status when its status is true
    When rc runs the script
      """
      rc -c 'true | true'; ~ $status '' && echo empty
      """
    Then it prints
      """
      /rc/lib/rcmain:24 *eval*:1: true: file does not exist: './true'
      /rc/lib/rcmain:24 *eval*:1: true: file does not exist: './true'
      """

  @FOG_RC_RUN_138
  Scenario: whatis finds only executable files in $path
    When rc runs the script
      """
      cd /tmp; echo echo hi >ne; whatis ne; echo $status
      """
    Then it prints
      """
      not found
      """

  @FOG_RC_RUN_139
  Scenario: sigexit gets the arguments of the function that exits
    When rc runs the script
      """
      fn sigexit {echo bye $*}
      fn f { exit }
      f a b
      """
    Then it prints
      """
      bye a b
      """

  @FOG_RC_RUN_140
  Scenario: The last line of a file without a newline runs with its descriptor reused
    When rc runs the script
      """
      echo hi >/tmp/dn
      echo -n 'cat </tmp/dn' >/tmp/dna
      . /tmp/dna
      echo after
      """
    Then it prints
      """
      /tmp/dna:1: token EOF: syntax error
      after
      """

  @FOG_RC_RUN_141
  Scenario: Patterns at the end of a name, complemented, reversed and with three- and four-byte classes
    When rc runs the script
      """
      ~ a a? || echo no single
      ~ '~' [~a] && echo tilde
      ~ b [z-a] && echo reversed
      ~ € [‐-‿] || echo euro not punctuation
      ~ € [«-®] || echo euro not latin
      ~ € [𐀀-􏿿] || echo euro not astral
      ~ € [,] || echo euro not comma
      ~ € [₠-₿] && echo euro currency
      mkdir -p /tmp/gg/a /tmp/gg/b; echo >/tmp/gg/a/x; echo /tmp/gg/*/x
      cd /tmp/gg; echo */x; echo /tmp/g?
      """
    Then it prints
      """
      no single
      tilde
      reversed
      euro not punctuation
      euro not latin
      euro not astral
      euro not comma
      euro currency
      /tmp/gg/a/x
      a/x
      /tmp/gg
      """

  @FOG_RC_RUN_142
  Scenario: Subscripts and positions with several digits
    When rc runs the script
      """
      x=(1 2 3 4 5 6 7 8 9 10 11 12)
      echo $x(11) $x(10-12)
      echo $x(11-)
      echo $x(2-20)
      echo $x(2-11)
      fn f { echo $12 $11; cat <<EOF
      $12 $1a $0 $2
      EOF
      }
      f a b c d e f g h i j k l
      """
    Then it prints
      """
      11 10 11 12
      11 12
      2 3 4 5 6 7 8 9 10 11 12
      2 3 4 5 6 7 8 9 10 11
      l k
      l  /tmp/s b
      """

  @FOG_RC_RUN_143
  Scenario: Concatenating empty lists
    When rc runs the script
      """
      x=(); echo $x^$x end
      echo a^$x end
      echo not reached
      """
    Then it prints
      """
      end
      /tmp/s:2: null list in concatenation
      """

  @FOG_RC_RUN_144
  Scenario: A pipeline's status after status was emptied
    When rc runs the script
      """
      status=(); echo a | cat; echo $#status
      """
    Then it prints
      """
      a
      1
      """

  @FOG_RC_RUN_145
  Scenario: A here document cut off at the end of a file
    When rc runs the script
      """
      echo -n 'cat <<EOF
      a$' >/tmp/he
      . /tmp/he
      echo after
      """
    Then it prints
      """
      aafter
      """

  @FOG_RC_RUN_146
  Scenario: rfork keeps the redirections waiting for a command
    When rc runs the script
      """
      {rfork n; echo hi} >/tmp/rf; cat /tmp/rf
      """
    Then it prints
      """
      hi
      """

  @FOG_RC_RUN_147
  Scenario: A syntax error in eval sets the status
    When rc runs the script
      """
      eval 'echo ('; ~ $status '' || echo failed
      """
    Then it prints
      """
      /tmp/s:1 *eval*:2: syntax error
      failed
      """

  @FOG_RC_RUN_148
  Scenario: A here document's file is removed when it is closed
    When rc runs the script
      """
      cat <<EOF
      hi
      EOF
      echo /tmp/here*
      """
    Then it prints
      """
      hi
      /tmp/here*
      """

  @FOG_RC_RUN_149
  Scenario: The last command of a forked child is exec'd in place of it
    When rc runs the script
      """
      {rc -c 'echo $pid' >/tmp/p1} &
      a=$apid; wait; ~ `{cat /tmp/p1} $a && echo in place
      {if(false) x=1; rc -c 'echo $pid' >/tmp/p2; if not {}} &
      a=$apid; wait; ~ `{cat /tmp/p2} $a && echo in place too
      {if(false) x=1; rc -c 'echo $pid' >/tmp/p3; if not echo not} &
      a=$apid; wait; ~ `{cat /tmp/p3} $a || echo forked
      """
    Then it prints
      """
      in place
      /tmp/s:4: `if not' does not follow `if(...)'
      """

  @FOG_RC_RUN_150
  Scenario: A backquote's words go before the arguments after it
    When rc runs the script
      """
      echo `{echo a} y
      x=`{echo a}; rc -c 'echo /fd/*'
      """
    Then it prints
      """
      a y
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl
      """

  @FOG_RC_RUN_151
  Scenario: shift and rfork pop their arguments after a usage message
    When rc runs the script
      """
      for(i in a b) {echo $i; shift 1 2 >[2]/dev/null}
      for(i in c d) {echo $i; rfork x >[2]/dev/null}
      """
    Then it prints
      """
      a
      Usage: shift [n]
      b
      Usage: shift [n]
      c
      Usage: rfork [fnesFNEm]
      d
      Usage: rfork [fnesFNEm]
      """

  @FOG_RC_RUN_152
  Scenario: cd with no argument goes to $home, and $cdpath names only the directories it adds
    When rc runs the script
      """
      cd; ~ $status '' && echo home
      cdpath=(. /); cd tmp
      cd /; cdpath=(/); cd tmp
      """
    Then it prints
      """
      home
      /tmp
      """

  @FOG_RC_RUN_153
  Scenario: A dot file's last line runs with its descriptor closed behind it
    When rc runs the script
      """
      echo hi >/tmp/dn
      echo 'cat </tmp/dn' >/tmp/dnb
      . /tmp/dnb
      echo after
      """
    Then it prints
      """
      hi
      after
      """

  @FOG_RC_RUN_154
  Scenario: rfork F forgets the redirections waiting for a command
    When rc runs the script
      """
      @{rfork F; echo hi >/tmp/rf1 >[2]/tmp/rf2 >[3]/tmp/rf3 >[4]/tmp/rf4 >[5]/tmp/rf5}
      cat /tmp/rf1
      @{rfork F; echo ho >[5]/tmp/rg5 >[4]/tmp/rg4 >[3]/tmp/rg3 >[2]/tmp/rg2 >/tmp/rg1}
      cat /tmp/rg1
      """
    Then it prints
      """
      hi
      ho
      """

  @FOG_RC_RUN_155
  Scenario: rc -m reads another file in place of rcmain, and -c's arguments are $*
    When rc runs the script
      """
      echo 'echo main $*' >/tmp/m1; rc -m /tmp/m1 a b
      rc -c 'echo $*' a b
      rc -m
      ~ $status *'bad flags' && echo bad flags
      rc -
      ~ $status *error && echo error
      """
    Then it prints
      """
      main a b
      a b
      Flag -m: too few arguments
      Usage: rc [-srdiIlxebpvV] [-c arg] [-m command] [file [arg ...]]
      bad flags
      /rc/lib/rcmain:38: Usage: . [-biq] file [arg ...]
      error
      """

  @FOG_RC_RUN_156
  Scenario: A dot file run last in a child gets the descriptors of the files before it
    When rc runs the script
      """
      echo hi >/tmp/dn
      echo 'cat </tmp/dn' >/tmp/dnc
      @{. /tmp/dnc}
      x=1 @{eval 'cat </tmp/dn'}
      """
    Then it prints
      """
      hi
      hi
      """

  @FOG_RC_RUN_157
  Scenario: >{} gives a command's input as a file to write
    When rc runs the script
      """
      x=`{@{echo hi > >{cat}}}; echo $x
      x=`{@{echo ho >>{cat}}}; echo $x
      """
    Then it prints
      """
      hi
      ho /fd/6
      """

  @FOG_RC_RUN_158
  Scenario: A byte that does not continue a UTF-8 sequence is a character of its own, and a broken sequence matches another broken one in a class
    When rc runs the script, its \x escapes bytes
      """
      ~ a\x80 a? && echo stray after ascii
      ~ \xE2\x82\xAC\x80 ?? && echo stray after three bytes
      ~ \xC2 [\xE2\x82] && echo cut two bytes match cut three
      ~ \xE2\x82 [\xC2] && echo cut three bytes match cut two
      ~ \xF0\x9F [\xC2] && echo cut four bytes match cut two
      ~ \x80\x80\x80\x80 [\xC2] && echo a continuation byte leads nothing
      """
    Then it prints
      """
      stray after ascii
      stray after three bytes
      cut two bytes match cut three
      cut three bytes match cut two
      cut four bytes match cut two
      a continuation byte leads nothing
      """

  @FOG_RC_RUN_159
  Scenario: A class compares four-byte sequences by their code points
    When rc runs the script, its \x escapes bytes
      """
      ~ \xF0\x9F\x98\x80 [a-\xEF\xBF\xBF] || echo above the BMP
      ~ \xF1\x80\x80\x80 [\xF0\x9F\x98\x80-\xF3\xB0\x80\x80] && echo the lead byte counts
      ~ \xF1\x90\x80\x80 [\xF1\x80\x80\x81-\xF2\x80\x80\x82] && echo the second byte counts
      ~ \xF1\x80\x90\x80 [\xF1\x80\x80\x81-\xF2\x80\x80\x82] && echo the third byte counts
      """
    Then it prints
      """
      above the BMP
      the lead byte counts
      the second byte counts
      the third byte counts
      """

  @FOG_RC_RUN_160
  Scenario: A here document copies the bytes after one from 0xA0 to 0xF7 without looking for $
    When rc runs the script, its \x escapes bytes
      """
      x=X
      cat <<EOF >/tmp/hb
      \x9F$x
      \xA0$x
      \xF5$x
      \xF6a$x
      \xF7a$x
      \xF8$x
      a\xF6
      EOF
      h=`{cat /tmp/hb}
      ~ $h(1) \x9FX && echo 9F substitutes
      ~ $h(2) '\xA0$x' && echo A0 copies one
      ~ $h(3) '\xF5$x' && echo F5 copies one
      ~ $h(4) '\xF6a$x' && echo F6 copies two
      ~ $h(5) '\xF7a$x' && echo F7 copies two
      ~ $h(6) \xF8X && echo F8 substitutes
      ~ $h(7) a\xF6 && echo F6 at the end
      """
    Then it prints
      """
      9F substitutes
      A0 copies one
      F5 copies one
      F6 copies two
      F7 copies two
      F8 substitutes
      F6 at the end
      """

  @FOG_RC_RUN_161
  Scenario: rc reports a pipeline it cannot make a pipe for
    Given rc starts with all but two of its 5000 descriptors in use
    When rc runs the script
      """
      echo a | cat
      """
    Then it prints
      """
      /tmp/s:1: can't get pipe: no free file descriptors
      """

  @FOG_RC_RUN_162
  Scenario: rc reports a <{} it cannot make a pipe for
    Given rc starts with all but two of its 5000 descriptors in use
    When rc runs the script
      """
      cat <{echo a}
      """
    Then it prints
      """
      /tmp/s:1: can't get pipe: no free file descriptors
      """

  @FOG_RC_RUN_163
  Scenario: Interactive rc goes on after a pipe it cannot make
    Given rc starts with all but two of its 5000 descriptors in use
    When rc -i reads the script
      """
      x=`{echo a}
      echo a | cat
      cat <{echo a}
      y=after; whatis y
      """
    Then it prints, ending with a prompt
      """
      % /fd/0:1: can't make pipe: no free file descriptors
      % /fd/0:2: can't get pipe: no free file descriptors
      % /fd/0:3: can't get pipe: no free file descriptors
      % y=after
      % 
      """

  @FOG_RC_RUN_164
  Scenario: rc reports each variable it cannot write to /env when its redirection took the last descriptor
    Given rc starts with all but two of its 5000 descriptors in use
    When rc runs the script
      """
      x=1
      echo hi >/tmp/q
      echo hi
      """
    Then it prints
      """
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      hi
      """

  @FOG_RC_RUN_165
  Scenario: rc reports an /env it cannot open
    Given rc starts with all of its 5000 descriptors in use
    When rc runs the script
      """
      echo hi
      """
    Then it prints
      """
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      /bin/rc: can't open: no free file descriptors
      """

  @FOG_RC_RUN_166
  Scenario: Here files are named for the shell's pid and a serial number
    When rc runs the script
      """
      x=(0 1 2 3 4 5 6 7 8 9)
      for(i in $x) cat /dev/null
      rc -c 'x=(0 1 2 3 4 5 6 7 8 9)
      for(i in $x $x 0 1 2 3 4 5 6 7) cat >/dev/null <<EOF
      x
      EOF
      {n=/tmp/here*^$pid^??; echo $#n; ~ $n /tmp/here??????????BC && echo ten; ~ $n /tmp/here*^$pid^BC && echo pid; ~ $n /tmp/here*BC && echo bc; ~ $n /tmp/here*C && echo c; ~ $n /tmp/here*B? && echo b} <<EOF
      y
      EOF
      '
      """
    Then it prints
      """
      1
      ten
      pid
      bc
      c
      b
      """

  @FOG_RC_RUN_167
  Scenario: $#n counts an argument that is there, and $1a is a variable
    When rc runs the script
      """
      fn g { echo $#2 $#3 $#1a; echo $1a $#0 }
      g a b
      """
    Then it prints
      """
      1 0 0
      1
      """

  @FOG_RC_RUN_168
  Scenario: cd . and cd .. are not looked for in $cdpath
    When rc runs the script
      """
      cdpath=(/tmp)
      cd /tmp; cd .; cd ..; echo ok
      """
    Then it prints
      """
      ok
      """

  @FOG_RC_RUN_169
  Scenario: A variable's /env file is named within rc's 128-byte buffer
    When rc runs the script
      """
      qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq=1
      rrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrr=2
      cat /dev/null
      x=(/env/q*); echo $#x
      ~ $x /env/qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq && echo 122
      y=(/env/r*); ~ $y /env/rrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrrr && echo 122 too
      """
    Then it prints
      """
      1
      122
      """

  @FOG_RC_RUN_170
  Scenario: A <> redirection that cannot open names itself
    When rc runs the script
      """
      cat <>/nonesuch/f
      """
    Then it prints
      """
      /tmp/s:1: <> can't open: /nonesuch/f: file does not exist: '/nonesuch'
      """

  @FOG_RC_RUN_171
  Scenario: An error sets the status error
    When rc runs the script
      """
      @{x=(a b); echo >$x}
      ~ $status *error && echo error
      """
    Then it prints
      """
      /tmp/s:1: > requires singleton
      error
      """

  @FOG_RC_RUN_172
  Scenario: A pipeline's status keeps a left side that finished first
    When rc runs the script
      """
      {exit left} | {cat; cat /dev/null}
      ~ $status *left^'|' && echo left first
      """
    Then it prints
      """
      left first
      """

  @FOG_RC_RUN_173
  Scenario: -I keeps rc from being interactive even with -i
    When rc runs the script
      """
      echo echo hi | rc -Ii
      """
    Then it prints
      """
      hi
      """

  @FOG_RC_RUN_174
  Scenario: The second word of $prompt prompts for more of a command, and a tab when there is none
    When rc -i reads the script
      """
      prompt=('$ ' '>> ')
      {echo a
      echo b}
      prompt=('# ')
      {echo c
      }
      """
    Then it prints, ending with a prompt
      """
      % $ >> a
      b
      $ # 	c
      # 
      """

  @FOG_RC_RUN_175
  Scenario: A login shell, whose argv[0] starts with -, tests for the profiles rcmain reads
    Given rc is started as a login shell
    When rc -i reads the script
      """
      echo hi
      """
    Then it prints, ending with a prompt
      """
      /rc/lib/rcmain:28: /bin/test: file does not exist: '/bin/test'
      /rc/lib/rcmain:29: /bin/test: file does not exist: '/bin/test'
      % hi
      % 
      """

  @FOG_RC_RUN_176
  Scenario: Case patterns made by concatenation or in parentheses are not globbed
    When rc runs the script
      """
      mkdir /tmp/cpg; cd /tmp/cpg; echo >cfile
      x=c
      switch(cx){case $x^*; echo concatenation matched}
      switch(cx){case (c*); echo parenthesized matched}
      """
    Then it prints
      """
      concatenation matched
      parenthesized matched
      """

  @FOG_RC_RUN_178
  Scenario: A function's return marks $* to be written to /env again
    When rc runs the script
      """
      *=(p q); cat /dev/null
      rc -c 'cat /dev/null'
      fn f {}; f; cat /dev/null
      x=`{cat '/env/*'}; echo $#x
      """
    Then it prints
      """
      2
      """

  @FOG_RC_RUN_179
  Scenario: A file moved off a clobbered descriptor is not left open
    When rc runs the script
      """
      rc -c 'echo /fd/*' >[5=1] >/tmp/cl4; cat /tmp/cl4
      """
    Then it prints
      """
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl /fd/5 /fd/5ctl
      """

  @FOG_RC_RUN_181
  Scenario: A redirection or here document that fails ends a subshell with its error
    When rc runs the script
      """
      @{cat </nonesuch}; ~ $status *sys:* || echo no crash
      """
    Then it prints
      """
      /tmp/s:1: < can't open: /nonesuch: file does not exist: '/nonesuch'
      no crash
      """

  @FOG_RC_RUN_184
  Scenario: rfork n leaves the redirection for the next command, which writes the file
    When rc runs the script
      """
      {rfork n; echo hi} >/tmp/rf; echo then; cat /tmp/rf
      """
    Then it prints
      """
      then
      hi
      """

  @FOG_RC_RUN_185
  Scenario: rfork F leaves a command only the descriptors redirected for it
    When rc runs the script
      """
      @{rfork F; rc -c 'echo /fd/*' >/tmp/rh1 >[2]/tmp/rh2 >[3]/tmp/rh3 >[4]/tmp/rh4 >[5]/tmp/rh5}; cat /tmp/rh1
      @{rfork F; rc -c 'echo /fd/*' >[5]/tmp/ri5 >[4]/tmp/ri4 >[3]/tmp/ri3 >[2]/tmp/ri2 >/tmp/ri1}; cat /tmp/ri1
      """
    Then it prints
      """
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl /fd/4 /fd/4ctl /fd/5 /fd/5ctl
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl /fd/4 /fd/4ctl /fd/5 /fd/5ctl
      """

  @FOG_RC_RUN_186
  Scenario: rc exits with an empty status for a true status of 0
    When rc runs the script
      """
      rc -c 'status=0'; ~ $status '' && echo empty
      """
    Then it prints
      """
      empty
      """

  @FOG_RC_RUN_188
  Scenario: A variable read from /env is not written back until it changes
    When rc runs the script
      """
      z=1; rc -c 'cd . >/env/z; y=`{cat /env/z}; echo $#y'
      """
    Then it prints
      """
      0
      """

  @FOG_RC_RUN_189
  Scenario: rc -s prints a false status before reading the next command
    When rc runs the script
      """
      rc -s -c 'status=bad
      y=1; whatis y'
      """
    Then it prints
      """
      status='flag not set'
      status='flag not set'
      y=1
      """

  @FOG_RC_RUN_190
  Scenario: A dot file's last line, read to its end, runs with the file's descriptor reused
    When rc runs the script
      """
      echo hi >/tmp/dn
      echo '{cat </tmp/dn}' >/tmp/dnd
      . /tmp/dnd
      echo after
      """
    Then it prints
      """
      hi
      after
      """

  @FOG_RC_RUN_191
  Scenario: An error in an interactive shell's backquote leaves the child reading commands as well
    When rc -i reads the script
      """
      x=`{cat </nonesuch}
      echo after
      """
    Then it prints, ending with a prompt
      """
      % /fd/0:1: < can't open: /nonesuch: file does not exist: '/nonesuch'
      % after
      % % after
      % 
      """

  @FOG_RC_RUN_192
  Scenario: A pipeline whose right side leaves no status keeps the left side's alone
    When rc runs the script
      """
      status=(); {exit bad} | x=1; ~ $status *bad && echo bad alone
      """
    Then it prints
      """
      bad alone
      """

  @FOG_RC_RUN_193
  Scenario: A pipeline's status keeps a left side that finished last
    When rc runs the script
      """
      {cat /dev/null; cat /dev/null; exit bad} | ~ a a; ~ $status *bad^'|' && echo left last
      """
    Then it prints
      """
      left last
      """

  @FOG_RC_RUN_194
  Scenario: . looks for a file in $path
    When rc runs the script
      """
      mkdir /tmp/pd; echo 'x=found; whatis x' >/tmp/pd/dotx; path=(/tmp/pd); . dotx
      """
    Then it prints
      """
      x=found
      """

  @FOG_RC_RUN_195
  Scenario: wait for a child already waited for returns at once
    When rc runs the script
      """
      x=(0 1 2 3 4 5 6 7 8 9)
      {for(i in $x) for(j in $x) cat /dev/null; exit slow} &
      s=$apid; {exit} & t=$apid; wait $t; wait $t; wait $s; ~ $status *slow && echo slow
      """
    Then it prints
      """
      slow
      """

  @FOG_RC_RUN_197
  Scenario: The child left reading commands after an error in a backquote has the shell's locals
    When rc -i reads the script
      """
      x=`{cat </nonesuch}
      echo $0
      """
    Then it prints, ending with a prompt
      """
      % /fd/0:1: < can't open: /nonesuch: file does not exist: '/nonesuch'
      % /fd/0
      % % /fd/0
      % 
      """

  @FOG_RC_RUN_198
  Scenario: A dot file read to its end by its last line runs that line with the file's descriptor reused
    When rc runs the script
      """
      echo hi >/tmp/dn
      echo 'if(~ a b) x=1
      if not {cat </tmp/dn}' >/tmp/dne
      . /tmp/dne
      echo after
      """
    Then it prints
      """
      hi
      after
      """

  @FOG_RC_RUN_201
  Scenario: The child left reading commands after an error in a backquote shares the shell's locals
    When rc -i reads the script
      """
      x=`{*=(a b); cat </nonesuch}
      echo $*
      """
    Then it prints, ending with a prompt
      """
      % /fd/0:1: < can't open: /nonesuch: file does not exist: '/nonesuch'
      % a b
      % % 
      % 
      """

  @FOG_RC_RUN_202
  Scenario: rfork F forgets a dup waiting for a command
    When rc runs the script
      """
      echo 'rc -c ''echo /fd/*'' >/tmp/rfo' >/tmp/rfd
      @{rfork F; . /tmp/rfd} >[5=0]
      cat /tmp/rfo
      """
    Then it prints
      """
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl
      """

  @FOG_RC_RUN_203
  Scenario: A here document with no file for it ends a shell with its error, not a crash
    When rc runs the script
      """
      x=(0 1 2 3 4 5 6 7 8 9)
      for(i in $x) cat /dev/null
      rc -c 'switch($pid){
      case ?
      	r=00000000
      case ??
      	r=0000000
      case ???
      	r=000000
      case ????
      	r=00000
      case ?????
      	r=0000
      }
      for(c in 0 a b c d e f g h i j k l m n o p q r s t u v w x) echo >/tmp/here^$c^$r^$pid^AA
      cat <<EOF
      x
      EOF
      '
      ~ $status *sys:* || echo no crash
      """
    Then it prints
      """
      /rc/lib/rcmain:24 *eval*:14: << can't get temp file: /tmp/hereN: 
      no crash
      """

  @FOG_RC_RUN_204
  Scenario: rc skips a file in /env it has no descriptor to read
    Given "/env/x" holds "1"
    And rc starts with all but one of its 5000 descriptors in use
    When rc runs the script
      """
      whatis x; whatis status
      """
    Then it prints
      """
      status=''
      """

  @FOG_RC_RUN_205
  Scenario: A file moved off a clobbered descriptor is closed in the shell too
    When rc runs the script
      """
      rc -c 'echo /fd/*' >[4=1] >/tmp/cl6; echo /fd/*
      """
    Then it prints
      """
      /fd/0 /fd/0ctl /fd/1 /fd/1ctl /fd/2 /fd/2ctl /fd/3 /fd/3ctl /fd/4 /fd/4ctl
      """

  @FOG_RC_RUN_206
  Scenario: An interactive shell goes on after a here document with no file for it
    When rc -i reads the script
      """
      switch($pid){
      case ?
      	r=00000000
      case ??
      	r=0000000
      case ???
      	r=000000
      case ????
      	r=00000
      case ?????
      	r=0000
      }
      for(c in 0 a b c d e f g h i j k l m n o p q r s t u v w x) echo >/tmp/here^$c^$r^$pid^AA
      cat <<EOF
      x
      EOF
      y=after; whatis y
      """
    Then it prints, ending with a prompt
      """
      % 											% % 		/fd/0:14: << can't get temp file: /tmp/hereN: file does not exist: '/env/fn#*'
      % y=after
      % 
      """

  @FOG_RC_RUN_207
  Scenario: bind's flags, repeated or combined, and its errors
    When rc runs the script
      """
      mkdir /tmp/ba /tmp/bb; echo >/tmp/ba/a; echo >/tmp/bb/b
      @{rfork n; bind -aa /tmp/bb /tmp/ba; echo /tmp/ba/*}
      @{rfork n; bind -bb /tmp/bb /tmp/ba; echo /tmp/ba/*}
      @{rfork n; bind -bcc /tmp/bb /tmp/ba; echo >/tmp/ba/c; echo /tmp/bb/*}
      @{rfork n; bind -bc /tmp/bb /tmp/ba; echo >/tmp/ba/d; echo /tmp/bb/*}
      @{rfork n; bind /tmp/bb /tmp/ba; ~ $status '' && echo bound; echo /tmp/ba/*}
      bind -q /nonesuch /tmp/ba; ~ $status '' && echo quiet
      bind /nonesuch /tmp/ba; ~ $status *bind && echo failed
      bind /tmp/ba /nonesuch
      bind /tmp/ba/a /tmp/ba; ~ $status *bind && echo inconsistent
      bind -ab /tmp/bb /tmp/ba; ~ $status *usage && echo usage
      bind -x a b
      bind a; ~ $status *usage && echo usage too
      """
    Then it prints
      """
      /tmp/ba/a /tmp/ba/b
      /tmp/ba/a /tmp/ba/b
      /tmp/bb/b /tmp/bb/c
      /tmp/bb/b /tmp/bb/c /tmp/bb/d
      bound
      /tmp/ba/b /tmp/ba/c /tmp/ba/d
      quiet
      bind: /nonesuch: file does not exist: '/nonesuch'
      failed
      bind: /nonesuch: file does not exist: '/nonesuch'
      bind /tmp/ba/a /tmp/ba: inconsistent mount
      inconsistent
      usage: bind [-b|-a|-c|-bc|-ac] new old
      usage
      usage: bind [-b|-a|-c|-bc|-ac] new old
      usage: bind [-b|-a|-c|-bc|-ac] new old
      usage too
      """

  @FOG_RC_RUN_208
  Scenario: mkdir's modes, flags, -- and errors
    When rc runs the script
      """
      mkdir /tmp/mk; cd /tmp/mk
      mkdir -m 1777 m1; mkdir -m 1000 m2; mkdir -m 777 m3; mkdir -m 77x m4; mkdir -m77 m5
      mkdir -m -1 m6; mkdir -m +7 m7; mkdir -m ' 7' m8; mkdir -m 77777777777777 m9; mkdir -m -77777777777777 ma
      mkdir -m; ~ $status *usage && echo usage
      echo *
      mkdir -; mkdir -- -x; mkdir -p -- -y/z; echo *
      mkdir e1 e1 e2; ~ $status *error && echo error
      mkdir -p e1 e3/f/g; ~ $status '' && echo made; echo e3/*/*
      mkdir -x
      mkdir
      """
    Then it prints
      """
      usage: mkdir [-p] [-m mode] dir...
      usage: mkdir [-p] [-m mode] dir...
      usage: mkdir [-p] [-m mode] dir...
      usage: mkdir [-p] [-m mode] dir...
      usage: mkdir [-p] [-m mode] dir...
      usage: mkdir [-p] [-m mode] dir...
      usage
      m3 m4 m5 m7 m8
      - -x -y m3 m4 m5 m7 m8
      mkdir: e1 already exists
      error
      made
      e3/f/g
      usage: mkdir [-p] [-m mode] dir...
      """

  @FOG_RC_RUN_209
  Scenario: cat's files, closed as it goes, and its errors
    When rc runs the script
      """
      echo hi >/tmp/cx
      cat /tmp/cx /fd/3
      cat /nonesuch; ~ $status *'can''t open'* && echo open status
      cat >[0]/tmp/cw; ~ $status *'error reading'* && echo read status
      cat /tmp/cx <[1]/tmp/cx; ~ $status *'write error'* && echo write status
      cat </tmp/cx
      """
    Then it prints
      """
      hi
      cat: can't open /fd/3: file does not exist: '/fd/3'
      cat: can't open /nonesuch: file does not exist: '/nonesuch'
      open status
      cat: error reading <stdin>: inappropriate use of fd
      read status
      cat: write error copying /tmp/cx: inappropriate use of fd
      write status
      hi
      """

  @FOG_RC_RUN_210
  Scenario: echo -n and echo's write error
    When rc runs the script
      """
      echo -n a; echo -n; echo b
      echo hi >/tmp/ex; echo hi <[1]/tmp/ex; ~ $status *'write error' && echo echo status
      """
    Then it prints
      """
      ab
      echo: write error: inappropriate use of fd
      echo status
      """

  @FOG_RC_RUN_211
  Scenario: mkdir reports a directory it cannot create and fails
    When rc runs the script
      """
      echo >/tmp/mf
      mkdir /tmp/mf/x; ~ $status *error && echo create error
      mkdir -p /tmp/mf/y/z; ~ $status *error && echo path error
      mkdir -p /tmp/mg/y; ~ $status '' && echo made
      """
    Then it prints
      """
      mkdir: can't create /tmp/mf/x: create in non-directory: '/tmp/mf/x'
      create error
      mkdir: can't create /tmp/mf/y: create in non-directory: '/tmp/mf/y'
      path error
      made
      """

  @FOG_RC_RUN_212
  Scenario: rc writes nothing for an empty buffer, so a backquote reading its errors reads on
    When rc runs the script
      """
      x=`{rc -c 'cat /dev/null; echo after' >[2=1]}; echo $x
      """
    Then it prints
      """
      after
      """
