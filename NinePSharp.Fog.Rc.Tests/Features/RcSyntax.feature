@fog_rc_syntax
Feature: rc reads commands as 9front rc does
  Fog's rc parses with the LALR(1) tables yacc builds from 9front's rc grammar, sys/src/cmd/rc/syn.y,
  and prints commands as pcmd.c does. A function's text, which whatis prints, is the printed form of
  its body, so whatis shows how rc read it. A script is read as rc reads a file, a line at a time,
  and reading stops at the first line with an error. Errors are reported as rc reports them, as
  file:line: token 'tok': message. The expected text below was recorded from 9front's rc. When
  FOG_9FRONT_ISO names a 9front ISO and qemu-system-x86_64 is installed, every scenario is also run
  through 9front's rc and must still match.

  @FOG_RC_SYN_001
  Scenario: A simple command
    When rc reads the script
      """
      fn f {echo hello world}
      """
    Then whatis f prints
      """
      fn f {
      	echo hello world
      }
      """

  @FOG_RC_SYN_002
  Scenario: Commands on one line are joined with semicolons, on separate lines by newlines
    When rc reads the script
      """
      fn f {
      a; b
      c
      }
      """
    Then whatis f prints
      """
      fn f {
      	a; b
      	c
      }
      """

  @FOG_RC_SYN_003
  Scenario: && and || keep their order
    When rc reads the script
      """
      fn f {a && b || c}
      """
    Then whatis f prints
      """
      fn f {
      	a && b || c
      }
      """

  @FOG_RC_SYN_004
  Scenario: A newline may follow &&, || and a pipe
    When rc reads the script
      """
      fn f {a &&
      b; c ||

      d; e |
      f; g |[2]
      h}
      """
    Then whatis f prints
      """
      fn f {
      	a && b
      	c || d
      	e|f
      	g|[2]h
      }
      """

  @FOG_RC_SYN_005
  Scenario: &&, ||, a pipe and & may have nothing on their left
    When rc reads the script
      """
      fn f {&& b; || c; | d; & &}
      """
    Then whatis f prints
      """
      fn f {
      	 && b;  || c; |d; &; &
      }
      """

  @FOG_RC_SYN_006
  Scenario: Pipes print their file descriptors, the second before the first
    When rc reads the script
      """
      fn f {a | b |[2] c |[2=3] d}
      """
    Then whatis f prints
      """
      fn f {
      	a|b|[2]c|[3=2]d
      }
      """

  @FOG_RC_SYN_007
  Scenario: Redirections move in front of their simple command
    When rc reads the script
      """
      fn f {a >f >>g <h <>i >[2]j >[2=1] >[3=]}
      """
    Then whatis f prints
      """
      fn f {
      	 >f  >>g  <h  <>i  >[2]j >[2=1]>[3=]a
      }
      """

  @FOG_RC_SYN_008
  Scenario: Redirections and pipes with explicit and multi-digit descriptors
    When rc reads the script
      """
      fn f {a >[10]f <[3]g <>[4]h >>[5]i |[3] b |[10=20] c >[1=] >[4=5]}
      """
    Then whatis f prints
      """
      fn f {
      	 >[10]f  <[3]g  <>[4]h  >>[5]i a|[3]b|[20=10]>[1=]>[4=5]c
      }
      """

  @FOG_RC_SYN_009
  Scenario: Dups and redirections before a command or a block
    When rc reads the script
      """
      fn f {>[2=1] cmd; >f >g {a}; x=1 >f cmd}
      """
    Then whatis f prints
      """
      fn f {
      	>[2=1]cmd;  >f  >g {
      		a
      	}; x=1  >f cmd
      }
      """

  @FOG_RC_SYN_010
  Scenario: >>[n=m], <<[n=m] and <>[n=m] are dups, as lex.c reads them
    When rc reads the script
      """
      fn f {echo >>[2=1]; cat <<[2=1]; echo <>[2=1]}
      """
    Then whatis f prints
      """
      fn f {
      	>[2=1]echo; >[2=1]cat; >[2=1]echo
      }
      """

  @FOG_RC_SYN_011
  Scenario: Pipes as file names, with descriptors
    When rc reads the script
      """
      fn f {cmp <[3]{a} >[4]{b}}
      """
    Then whatis f prints
      """
      fn f {
      	cmp  <[3]{
      		a
      	}  >[4]{
      		b
      	}
      }
      """

  @FOG_RC_SYN_012
  Scenario: Variable references, counts, joins, subscripts and indirection
    When rc reads the script
      """
      fn f {echo $x $#x $"x $x(1 2) $$y}
      """
    Then whatis f prints
      """
      fn f {
      	echo $x $#x $"x $x(1 2) $$y
      }
      """

  @FOG_RC_SYN_013
  Scenario: A variable name ends at the first character that cannot be in one
    When rc reads the script
      """
      fn f {echo $x.y $x-y $* $1 $x_y $é $#* $"* $x$y$z}
      """
    Then whatis f prints
      """
      fn f {
      	echo $x^.y $x^-y $* $1 $x_y $é $#* $"* $x^$y^$z
      }
      """

  @FOG_RC_SYN_014
  Scenario: Assignments before a command and on their own
    When rc reads the script
      """
      fn f {x=1 y=(a b) cmd; z=(); w=()}
      """
    Then whatis f prints
      """
      fn f {
      	x=1 y=(a b) cmd; z=(); w=()
      }
      """

  @FOG_RC_SYN_015
  Scenario: Implicit carets are made explicit
    When rc reads the script
      """
      fn f {echo a^b $x^.c $x.c $x$y '$'$x}
      """
    Then whatis f prints
      """
      fn f {
      	echo a^b $x^.c $x^.c $x^$y '$'^$x
      }
      """

  @FOG_RC_SYN_016
  Scenario: Carets join substitutions, quoted words and lists
    When rc reads the script
      """
      fn f {echo a^`{b} `{c}^d a^'b'^$c ($a $b)^($c)}
      """
    Then whatis f prints
      """
      fn f {
      	echo a^`{
      		b
      	} `{
      		c
      	}^d a^'b'^$c ($a $b)^($c)
      }
      """

  @FOG_RC_SYN_017
  Scenario: Quoted words keep rc quoting
    When rc reads the script
      """
      fn f {echo 'a b' 'it''s' '' 'tab	here' x'y'z}
      """
    Then whatis f prints
      """
      fn f {
      	echo 'a b' 'it''s' '' 'tab	here' x^'y'^z
      }
      """

  @FOG_RC_SYN_018
  Scenario: Words and quoted words hold UTF-8
    When rc reads the script
      """
      fn f {echo é 中 😀 'é中😀' aé$xé}
      """
    Then whatis f prints
      """
      fn f {
      	echo é 中 😀 'é中😀' aé^$xé
      }
      """

  @FOG_RC_SYN_019
  Scenario: A backslash not before a newline is an ordinary character
    When rc reads the script
      """
      fn f {echo a\b 'c\d' \}
      """
    Then whatis f prints
      """
      fn f {
      	echo a\b 'c\d' \
      }
      """

  @FOG_RC_SYN_020
  Scenario: Glob characters print without their markers
    When rc reads the script
      """
      fn f {echo *.c a?b [a-z]* '*'}
      """
    Then whatis f prints
      """
      fn f {
      	echo *.c a?b [a-z]* '*'
      }
      """

  @FOG_RC_SYN_021
  Scenario: Globs in assignments, lists, subscripts and concatenations
    When rc reads the script
      """
      fn f {x=*.c y=(a *.c) cmd $x(*) [ab]^c}
      """
    Then whatis f prints
      """
      fn f {
      	x=*.c y=(a *.c) cmd $x(*) [ab]^c
      }
      """

  @FOG_RC_SYN_022
  Scenario: Globs in parenthesized lists
    When rc reads the script
      """
      fn f {echo (a *.c b?) (*)}
      """
    Then whatis f prints
      """
      fn f {
      	echo (a *.c b?) (*)
      }
      """

  @FOG_RC_SYN_023
  Scenario: if and if not
    When rc reads the script
      """
      fn f {if(test -e x) echo yes
      if not echo no}
      """
    Then whatis f prints
      """
      fn f {
      	if(test -e x)echo yes
      	if not echo no
      }
      """

  @FOG_RC_SYN_024
  Scenario: if not can hold another if
    When rc reads the script
      """
      fn f {if(a) b
      if not if(c) d
      if not e}
      """
    Then whatis f prints
      """
      fn f {
      	if(a)b
      	if not if(c)d
      	if not e
      }
      """

  @FOG_RC_SYN_025
  Scenario: while
    When rc reads the script
      """
      fn f {while(~ $#x 0) x=1}
      """
    Then whatis f prints
      """
      fn f {
      	while (~ $#x 0)x=1
      }
      """

  @FOG_RC_SYN_026
  Scenario: for over a list, over the arguments, and over the empty list
    When rc reads the script
      """
      fn f {for(i in a b) echo $i; for(i) echo $i; for(i in ) echo never}
      """
    Then whatis f prints
      """
      fn f {
      	for(i in a b)echo $i; for(i)echo $i; for(i in ())echo never
      }
      """

  @FOG_RC_SYN_027
  Scenario: switch with cases
    When rc reads the script
      """
      fn f {switch($x){
      case a b
      	echo ab
      case *
      	echo other
      }}
      """
    Then whatis f prints
      """
      fn f {
      	switch ($x) {
      		case a b
      		echo ab
      		case *
      		echo other
      	}
      }
      """

  @FOG_RC_SYN_028
  Scenario: switch cases with several commands and an empty case
    When rc reads the script
      """
      fn f {switch($x){case a
      b; c
      case
      d
      }}
      """
    Then whatis f prints
      """
      fn f {
      	switch ($x) {
      		case a
      		b; c
      		case
      		d
      	}
      }
      """

  @FOG_RC_SYN_029
  Scenario: A switch case on one line, and a function with no name
    When rc reads the script
      """
      fn f {switch($x){case a; b}; fn {x}}
      """
    Then whatis f prints
      """
      fn f {
      	switch ($x) {
      		case a; b
      	}; fn  {
      		x
      	}
      }
      """

  @FOG_RC_SYN_030
  Scenario: Pattern matching with ~
    When rc reads the script
      """
      fn f {~ $x a* b}
      """
    Then whatis f prints
      """
      fn f {
      	~ $x a* b
      }
      """

  @FOG_RC_SYN_031
  Scenario: Negation and subshells
    When rc reads the script
      """
      fn f {! test -e x; @ {cd /tmp; ls}}
      """
    Then whatis f prints
      """
      fn f {
      	! test -e x; @ {
      		cd /tmp; ls
      	}
      }
      """

  @FOG_RC_SYN_032
  Scenario: Negation and subshells combined, and ~ with a list
    When rc reads the script
      """
      fn f {@ ! a; ! @ b; ~ $x (a b) c}
      """
    Then whatis f prints
      """
      fn f {
      	@ ! a; ! @ b; ~ $x (a b) c
      }
      """

  @FOG_RC_SYN_033
  Scenario: Command substitution, with and without a separator list
    When rc reads the script
      """
      fn f {echo `{date} `x{cat}}
      """
    Then whatis f prints
      """
      fn f {
      	echo `{
      		date
      	} `x{
      		cat
      	}
      }
      """

  @FOG_RC_SYN_034
  Scenario: Pipes as file names
    When rc reads the script
      """
      fn f {cmp <{a} <{b} >{c}}
      """
    Then whatis f prints
      """
      fn f {
      	cmp  <{
      		a
      	}  <{
      		b
      	}  >{
      		c
      	}
      }
      """

  @FOG_RC_SYN_035
  Scenario: Function definitions and deletions inside a function
    When rc reads the script
      """
      fn f {fn g {echo inner}; fn h}
      """
    Then whatis f prints
      """
      fn f {
      	fn g {
      		echo inner
      	}; fn h 
      }
      """

  @FOG_RC_SYN_036
  Scenario: Functions defined and deleted several names at once
    When rc reads the script
      """
      fn f {fn a b {x}; fn c d}
      """
    Then whatis f prints
      """
      fn f {
      	fn a b {
      		x
      	}; fn c d 
      }
      """

  @FOG_RC_SYN_037
  Scenario: A braced block with a redirection
    When rc reads the script
      """
      fn f {{a; b} >f}
      """
    Then whatis f prints
      """
      fn f {
      	 >f {
      		a; b
      	}
      }
      """

  @FOG_RC_SYN_038
  Scenario: Indentation wraps after eight tabs
    When rc reads the script
      """
      fn f {{{{{{{{{{a}}}}}}}}}}
      """
    Then whatis f prints
      """
      fn f {
      	{
      		{
      			{
      				{
      					{
      						{
      							{
      {
      	{
      		a
      	}
      }
      							}
      						}
      					}
      				}
      			}
      		}
      	}
      }
      """

  @FOG_RC_SYN_039
  Scenario: Background commands
    When rc reads the script
      """
      fn f {a & b &}
      """
    Then whatis f prints
      """
      fn f {
      	a&; b&
      }
      """

  @FOG_RC_SYN_040
  Scenario: Empty commands between separators are dropped
    When rc reads the script
      """
      fn f {a;;b; ; c&; &}
      """
    Then whatis f prints
      """
      fn f {
      	a; b; c&; &
      }
      """

  @FOG_RC_SYN_041
  Scenario: Comments are dropped
    When rc reads the script
      """
      fn f {a # comment
      b}
      """
    Then whatis f prints
      """
      fn f {
      	a
      	b
      }
      """

  @FOG_RC_SYN_042
  Scenario: A backslash-newline does not continue a comment
    When rc reads the script
      """
      fn f {a # comment \
      b}
      """
    Then whatis f prints
      """
      fn f {
      	a
      	b
      }
      """

  @FOG_RC_SYN_043
  Scenario: A comment may end a line
    When rc reads the script
      """
      fn f {a}
      """
    Then whatis f prints
      """
      fn f {
      	a
      }
      """

  @FOG_RC_SYN_044
  Scenario: A backslash-newline continues the line
    When rc reads the script
      """
      fn f {echo a \
       b}
      """
    Then whatis f prints
      """
      fn f {
      	echo a b
      }
      """

  @FOG_RC_SYN_045
  Scenario: Keywords in argument position are words
    When rc reads the script
      """
      fn f {echo ! @ ~ for}
      """
    Then whatis f prints
      """
      fn f {
      	echo ! @ ~ for
      }
      """

  @FOG_RC_SYN_046
  Scenario: Keyword names in argument position are words
    When rc reads the script
      """
      fn f {echo for in while if not switch fn}
      """
    Then whatis f prints
      """
      fn f {
      	echo for in while if not switch fn
      }
      """

  @FOG_RC_SYN_047
  Scenario: Here documents, substituted and quoted
    When rc reads the script
      """
      fn f {cat <<EOF
      hello $x
      EOF
      cat <<'EOF'
      raw $x
      EOF
      }
      """
    Then whatis f prints
      """
      fn f {
      	 cat <<EOF
      hello $x
      EOF

      	 cat <<'EOF'
      raw $x
      EOF

      }
      """

  @FOG_RC_SYN_048
  Scenario: Two here documents on one line
    When rc reads the script
      """
      fn f {cat <<A <<B
      a1
      A
      b1
      B
      }
      """
    Then whatis f prints
      """
      fn f {
      	  cat <<B
      b1
      B
       <<A
      a1
      A

      }
      """

  @FOG_RC_SYN_049
  Scenario: Only a line that is exactly the tag ends a here document
    When rc reads the script
      """
      fn f {cat <<EOF
      EO
      EOFX
      xEOF
      EOF
      }
      """
    Then whatis f prints
      """
      fn f {
      	 cat <<EOF
      EO
      EOFX
      xEOF
      EOF

      }
      """

  @FOG_RC_SYN_050
  Scenario: A quoted here document tag
    When rc reads the script
      """
      fn f {cat <<'E F'
      x
      E F
      }
      """
    Then whatis f prints
      """
      fn f {
      	 cat <<'E F'
      x
      E F

      }
      """

  @FOG_RC_SYN_051
  Scenario: Nested blocks indent
    When rc reads the script
      """
      fn f {if(a){if(b) c
      if not d}}
      """
    Then whatis f prints
      """
      fn f {
      	if(a){
      		if(b)c
      		if not d
      	}
      }
      """

  @FOG_RC_SYN_052
  Scenario: An unbalanced brace is a syntax error
    When rc reads the script
      """
      echo a }
      """
    Then rc reports
      """
      /tmp/s:1: token '}': syntax error
      """

  @FOG_RC_SYN_053
  Scenario: An unbalanced parenthesis is a syntax error
    When rc reads the script
      """
      )
      """
    Then rc reports
      """
      /tmp/s:1: token ')': syntax error
      """

  @FOG_RC_SYN_054
  Scenario: An unfinished if is a syntax error at end of file
    When rc reads the script
      """
      if(
      """
    Then rc reports
      """
      /tmp/s:2: token EOF: syntax error
      """

  @FOG_RC_SYN_055
  Scenario: An unclosed brace is a syntax error at end of file
    When rc reads the script
      """
      {a
      """
    Then rc reports
      """
      /tmp/s:2: token EOF: syntax error
      """

  @FOG_RC_SYN_056
  Scenario: An unclosed quote reads to the end of the file
    When rc reads the script
      """
      echo 'abc
      """
    Then rc reports
      """
      /tmp/s:2: token EOF: syntax error
      """

  @FOG_RC_SYN_057
  Scenario: An unclosed for list is a syntax error at the newline
    When rc reads the script
      """
      for(i in a b
      echo
      """
    Then rc reports
      """
      /tmp/s:2: syntax error
      """

  @FOG_RC_SYN_058
  Scenario: A count, $#, needs a word
    When rc reads the script
      """
      echo $#
      """
    Then rc reports
      """
      /tmp/s:2: syntax error
      """

  @FOG_RC_SYN_059
  Scenario: A join, $", needs a word
    When rc reads the script
      """
      echo $"
      """
    Then rc reports
      """
      /tmp/s:2: syntax error
      """

  @FOG_RC_SYN_060
  Scenario: A variable reference, $, needs a word
    When rc reads the script
      """
      echo $
      """
    Then rc reports
      """
      /tmp/s:2: syntax error
      """

  @FOG_RC_SYN_061
  Scenario: An unclosed subscript is a syntax error
    When rc reads the script
      """
      echo $x(
      """
    Then rc reports
      """
      /tmp/s:2: syntax error
      """

  @FOG_RC_SYN_062
  Scenario: A caret needs a word before it
    When rc reads the script
      """
      ^ b
      """
    Then rc reports
      """
      /tmp/s:1: token '^': syntax error
      """

  @FOG_RC_SYN_063
  Scenario: An assignment needs a name
    When rc reads the script
      """
      = b
      """
    Then rc reports
      """
      /tmp/s:1: token '=': syntax error
      """

  @FOG_RC_SYN_064
  Scenario: Separators may repeat, but a stray parenthesis may not
    When rc reads the script
      """
      a; ; ;; ; )
      """
    Then rc reports
      """
      /tmp/s:1: token ')': syntax error
      """

  @FOG_RC_SYN_065
  Scenario: in is a keyword, not a command
    When rc reads the script
      """
      in
      """
    Then rc reports
      """
      /tmp/s:1: token in: syntax error
      """

  @FOG_RC_SYN_066
  Scenario: A newline after switch's word is a syntax error, since yacc has already read it
    When rc reads the script
      """
      fn f {if(a)

      b
      for(i)
      c
      for(i in x)
      d
      while(e)
      f
      switch $x
      {case y
      g}
      if not
      h}
      whatis f
      """
    Then rc reports
      """
      /tmp/s:11: syntax error
      """

  @FOG_RC_SYN_067
  Scenario: A backquote separator must be a word
    When rc reads the script
      """
      fn f {echo `` '' {a} `'x'{b} `$x{c}}
      whatis f
      """
    Then rc reports
      """
      /tmp/s:1: token '`': syntax error
      """

  @FOG_RC_SYN_068
  Scenario: A malformed redirection is reported, and the rest of the line is skipped
    When rc reads the script
      """
      echo >[x] f
      """
    Then rc reports
      """
      /tmp/s:1: token '>[x': redirection syntax
      /tmp/s:2: token '>[x': syntax error
      """

  @FOG_RC_SYN_069
  Scenario: An unfinished redirection descriptor is reported with the newline
    When rc reads the script
      """
      echo >[
      """
    Then rc reports
      """
      /tmp/s:2: token '>[
      ': redirection syntax
      /tmp/s:2: token '>[
      ': syntax error
      """

  @FOG_RC_SYN_070
  Scenario: A redirection descriptor without its bracket is reported with the token rc collected
    When rc reads the script
      """
      echo >[1
      """
    Then rc reports
      """
      /tmp/s:2: token '>[11': redirection syntax
      /tmp/s:2: token '>[11': syntax error
      """

  @FOG_RC_SYN_071
  Scenario: A pipe may not close its descriptor
    When rc reads the script
      """
      a |[2=] b
      """
    Then rc reports
      """
      /tmp/s:1: token '|[22=': pipe syntax
      /tmp/s:2: token '|[22=': syntax error
      """

  @FOG_RC_SYN_072
  Scenario: A pipe descriptor must be a number
    When rc reads the script
      """
      a |[x b
      """
    Then rc reports
      """
      /tmp/s:1: token '|[x': pipe syntax
      /tmp/s:2: token '|[x': syntax error
      """

  @FOG_RC_SYN_073
  Scenario: A pipe descriptor needs its bracket
    When rc reads the script
      """
      a |[2 b
      """
    Then rc reports
      """
      /tmp/s:1: token '|[22': pipe syntax
      /tmp/s:2: token '|[22': syntax error
      """

  @FOG_RC_SYN_074
  Scenario: A here document's tag must be a word
    When rc reads the script
      """
      cat <<$x
      """
    Then rc reports
      """
      /tmp/s:2: Bad here tag
      """

  @FOG_RC_SYN_075
  Scenario: A here document runs to the end of the file
    When rc reads the script
      """
      fn f {cat <<EOF
      never ended
      """
    Then rc reports
      """
      /tmp/s:3: token EOF: syntax error
      """

  @FOG_RC_SYN_076
  Scenario: rc stops reading a script at its first syntax error
    When rc reads the script
      """
      fn g {echo g}
      echo )
      fn f {echo f}
      whatis f
      whatis g
      """
    Then rc reports
      """
      /tmp/s:2: token ')': syntax error
      """

  @FOG_RC_SYN_077
  Scenario: A word longer than rc's token buffer is reported, and the line is skipped
    When rc reads a script of "echo " and a word of 8200 x's
    Then rc reports the first 8191 x's as a token too long, then a syntax error at end of file

  @FOG_RC_SYN_078
  Scenario: Nesting deeper than yacc's stack is reported
    When rc reads a script of 600 opening parentheses
    Then rc reports
      """
      /tmp/s:1: token '(': yacc stack overflow
      """

  @FOG_RC_SYN_079
  Scenario: A NUL byte in a here document is an error
    When rc reads a here document holding a NUL byte
    Then rc reports
      """
      /tmp/s:2: NUL bytes in here doc
      """

  @FOG_RC_SYN_080
  Scenario: A 0xFF byte ends a here document, as rc reads it into a char that then equals EOF
    When rc reads a here document holding a 0xFF byte
    Then whatis f prints
      """
      fn f {
      	 cat <<X
      abX

      	cd
      	X
      }
      """

  @FOG_RC_SYN_081
  Scenario: ! and @ alone negate and fork an empty command
    When rc reads the script
      """
      fn f {!; @}
      """
    Then whatis f prints
      """
      fn f {
      	! ; @ 
      }
      """

  @FOG_RC_SYN_082
  Scenario: A quoted variable name ends the name, and the next word is joined to it
    When rc reads the script
      """
      fn f {echo $'x'.y.z $x'y'.z}
      """
    Then whatis f prints
      """
      fn f {
      	echo $'x'^.y.z $x^'y'^.z
      }
      """

  @FOG_RC_SYN_083
  Scenario: Descriptors from 0 to 9 and beyond
    When rc reads the script
      """
      fn f {a >[0]f >[9]g >[2=0] >[9=9] >[19=90] |[0] b |[9=0] c}
      """
    Then whatis f prints
      """
      fn f {
      	 >[0]f  >[9]g >[2=0]>[9=9]>[19=90]a|[0]b|[0=9]c
      }
      """

  @FOG_RC_SYN_084
  Scenario: A count where no word may be is reported as $#
    When rc reads the script
      """
      {a} $#x
      """
    Then rc reports
      """
      /tmp/s:1: token '$#': syntax error
      """

  @FOG_RC_SYN_085
  Scenario: A join where no word may be is reported as $"
    When rc reads the script
      """
      {a} $"x
      """
    Then rc reports
      """
      /tmp/s:1: token '$"': syntax error
      """

  @FOG_RC_SYN_086
  Scenario: A variable reference where no word may be is reported as $
    When rc reads the script
      """
      {a} $x
      """
    Then rc reports
      """
      /tmp/s:1: token '$': syntax error
      """

  @FOG_RC_SYN_087
  Scenario: An and inside a list is reported
    When rc reads the script
      """
      echo (a && b)
      """
    Then rc reports
      """
      /tmp/s:1: token '&&': syntax error
      """

  @FOG_RC_SYN_088
  Scenario: A background ampersand inside a list is reported
    When rc reads the script
      """
      echo (a & b)
      """
    Then rc reports
      """
      /tmp/s:1: token '&': syntax error
      """

  @FOG_RC_SYN_089
  Scenario: An or inside a list is reported
    When rc reads the script
      """
      echo (a || b)
      """
    Then rc reports
      """
      /tmp/s:1: token '||': syntax error
      """

  @FOG_RC_SYN_090
  Scenario: A pipe inside a list is reported
    When rc reads the script
      """
      echo (a | b)
      """
    Then rc reports
      """
      /tmp/s:1: token '|': syntax error
      """

  @FOG_RC_SYN_091
  Scenario: An appending redirection inside a list may begin a pipe as a file name, so its target is reported
    When rc reads the script
      """
      echo (a >>f)
      """
    Then rc reports
      """
      /tmp/s:1: token f: syntax error
      """

  @FOG_RC_SYN_092
  Scenario: A here document inside a list is reported at its tag
    When rc reads the script
      """
      echo (a <<f)
      """
    Then rc reports
      """
      /tmp/s:1: token f: syntax error
      """

  @FOG_RC_SYN_093
  Scenario: A read-write redirection inside a list is reported at its file
    When rc reads the script
      """
      echo (a <>f)
      """
    Then rc reports
      """
      /tmp/s:1: token f: syntax error
      """

  @FOG_RC_SYN_094
  Scenario: A reading redirection inside a list is reported at its file
    When rc reads the script
      """
      echo (a <f)
      """
    Then rc reports
      """
      /tmp/s:1: token f: syntax error
      """

  @FOG_RC_SYN_095
  Scenario: A redirection with a descriptor inside a list is reported at its file
    When rc reads the script
      """
      echo (a >[2]f)
      """
    Then rc reports
      """
      /tmp/s:1: token f: syntax error
      """

  @FOG_RC_SYN_096
  Scenario: A dup inside a list is reported with the token rc collected
    When rc reads the script
      """
      echo (a >[2=10])
      """
    Then rc reports
      """
      /tmp/s:1: token '>[22=10]': syntax error
      """

  @FOG_RC_SYN_097
  Scenario: A close inside a list is reported with the token rc collected
    When rc reads the script
      """
      echo (a >[2=])
      """
    Then rc reports
      """
      /tmp/s:1: token '>[22=]': syntax error
      """

  @FOG_RC_SYN_098
  Scenario: A pipe with descriptors inside a list is reported with the token rc collected
    When rc reads the script
      """
      echo (a |[2=10] b)
      """
    Then rc reports
      """
      /tmp/s:1: token '|[22=10]': syntax error
      """

  @FOG_RC_SYN_099
  Scenario: A parenthesis after a word that is not a variable is a subscript out of place
    When rc reads the script
      """
      echo a(b)
      """
    Then rc reports
      """
      /tmp/s:1: token '( [SUB]': syntax error
      """

  @FOG_RC_SYN_100
  Scenario: A token with a space is quoted in the report
    When rc reads the script
      """
      {a} 'a b'
      """
    Then rc reports
      """
      /tmp/s:1: token 'a b': syntax error
      """

  @FOG_RC_SYN_101
  Scenario: A token of non-ASCII bytes is not quoted in the report
    When rc reads the script
      """
      {a} Ā
      """
    Then rc reports
      """
      /tmp/s:1: token Ā: syntax error
      """

  @FOG_RC_SYN_102
  Scenario: A token of UTF-8 is reported as it is
    When rc reads the script
      """
      {a} é
      """
    Then rc reports
      """
      /tmp/s:1: token é: syntax error
      """

  @FOG_RC_SYN_103
  Scenario: not without if is reported
    When rc reads the script
      """
      not x
      """
    Then rc reports
      """
      /tmp/s:1: token not: syntax error
      """

  @FOG_RC_SYN_104
  Scenario: ~ needs a word
    When rc reads the script
      """
      ~
      """
    Then rc reports
      """
      /tmp/s:2: syntax error
      """

  @FOG_RC_SYN_105
  Scenario: A here document error skips the rest of the line, and reading continues to the next error
    When rc reads the script
      """
      fn f {cat <<$x}
      whatis f
      """
    Then rc reports
      """
      /tmp/s:1: token '}': Bad here tag
      /tmp/s:2: token whatis: syntax error
      """

  @FOG_RC_SYN_106
  Scenario: A second descriptor may have several digits
    When rc reads the script
      """
      fn f {a >[2=19] |[2=19] b}
      """
    Then whatis f prints
      """
      fn f {
      	>[2=19]a|[19=2]b
      }
      """

  @FOG_RC_SYN_107
  Scenario: A backslash-newline inside quotes is kept
    When rc reads the script
      """
      fn f {echo 'a\
      b'}
      """
    Then whatis f prints
      """
      fn f {
      	echo 'a\
      b'
      }
      """

  @FOG_RC_SYN_108
  Scenario: A backslash-newline after a comment line continues the line
    When rc reads the script
      """
      fn f {# c
      echo a \
      b}
      """
    Then whatis f prints
      """
      fn f {
      	echo a b
      }
      """

  @FOG_RC_SYN_109
  Scenario: A here document may be empty
    When rc reads the script
      """
      fn f {cat <<X
      X
      }
      """
    Then whatis f prints
      """
      fn f {
      	 cat <<X
      X

      }
      """

  @FOG_RC_SYN_110
  Scenario: Blanks may come between && and the newline after it
    When rc reads the script
      """
      fn f {a && 
      b}
      """
    Then whatis f prints
      """
      fn f {
      	a && b
      }
      """

  @FOG_RC_SYN_111
  Scenario: A block's redirections are all kept
    When rc reads the script
      """
      fn f {{a} >f >g}
      """
    Then whatis f prints
      """
      fn f {
      	 >f  >g {
      		a
      	}
      }
      """

  @FOG_RC_SYN_112
  Scenario: A here document at the top level is read after its line
    When rc reads the script
      """
      ~ a b && cat <<X
      )
      X
      fn f {a}
      """
    Then whatis f prints
      """
      fn f {
      	a
      }
      """

  @FOG_RC_SYN_113
  Scenario: Newlines may follow if, if not, for and while before their command
    When rc reads the script
      """
      fn f {if(a)

      b
      if not

      c
      for(i in x)

      d
      for(i)

      e
      while(g)

      h}
      """
    Then whatis f prints
      """
      fn f {
      	if(a)b
      	if not c
      	for(i in x)d
      	for(i)e
      	while (g)h
      }
      """

  @FOG_RC_SYN_114
  Scenario: A keyword used as a word joins what follows it
    When rc reads the script
      """
      fn f {echo for'x' in$y}
      """
    Then whatis f prints
      """
      fn f {
      	echo for^'x' in^$y
      }
      """

  @FOG_RC_SYN_115
  Scenario: A command continued onto the next line keeps its sequence on one line
    When rc reads the script
      """
      fn f {a; b &&
      c}
      """
    Then whatis f prints
      """
      fn f {
      	a; b && c
      }
      """

  @FOG_RC_SYN_116
  Scenario: A command substitution starting on its line is a command of its own
    When rc reads the script
      """
      fn f {x; `{
      a}}
      """
    Then whatis f prints
      """
      fn f {
      	x
      	`{
      		a
      	}
      }
      """

  @FOG_RC_SYN_117
  Scenario: An appending redirection's descriptor is reported with the token rc collected
    When rc reads the script
      """
      echo >>[x
      """
    Then rc reports
      """
      /tmp/s:1: token '>>[x': redirection syntax
      /tmp/s:2: token '>>[x': syntax error
      """

  @FOG_RC_SYN_118
  Scenario: A here document's descriptor is reported with the token rc collected
    When rc reads the script
      """
      cat <<[x
      """
    Then rc reports
      """
      /tmp/s:1: token '<<[x': redirection syntax
      /tmp/s:2: token '<<[x': syntax error
      """

  @FOG_RC_SYN_119
  Scenario: A read-write redirection's descriptor is reported with the token rc collected, which repeats the <
    When rc reads the script
      """
      echo <>[x
      """
    Then rc reports
      """
      /tmp/s:1: token '<<[x': redirection syntax
      /tmp/s:2: token '<<[x': syntax error
      """

  @FOG_RC_SYN_120
  Scenario: Lines are counted across a backslash-newline
    When rc reads the script
      """
      echo a \
      )
      """
    Then rc reports
      """
      /tmp/s:2: token ')': syntax error
      """

  @FOG_RC_SYN_121
  Scenario: A word that overflows the token buffer takes in the next line's word
    When rc reads a script of "echo " and a word of 8200 x's, then a line of "yyy"
    Then rc reports the first 8191 x's as a token too long

  @FOG_RC_SYN_122
  Scenario: 249 opening parentheses still fit yacc's stack
    When rc reads a script of 249 opening parentheses
    Then rc reports
      """
      /tmp/s:2: syntax error
      """

  @FOG_RC_SYN_123
  Scenario: 250 opening parentheses overflow yacc's stack
    When rc reads a script of 250 opening parentheses
    Then rc reports
      """
      /tmp/s:1: token '(': yacc stack overflow
      """

  @FOG_RC_SYN_124
  Scenario: A command substitution is on the line it starts on, not the line its brace closes on
    When rc reads the script
      """
      fn f {x; `{a
      }}
      """
    Then whatis f prints
      """
      fn f {
      	x; `{
      		a
      	}
      }
      """

  @FOG_RC_SYN_125
  Scenario: A line with a bad here document tag is not run, even when the next line completes it
    When rc reads the script
      """
      cat <<$x

      fn f {a}
      whatis f
      """
    Then rc reports
      """
      /tmp/s:2: Bad here tag
      """

  @FOG_RC_SYN_126
  Scenario: A word that overflowed the token buffer is reported as its first 8191 bytes
    When rc reads a script of "{a} " and a word of 8200 x's, then a line of "yyy"
    Then rc reports the first 8191 x's as a token too long, then as a token out of place on line 2

  @FOG_RC_SYN_126
  Scenario: A here or append redirection with an empty = closes the descriptor
    When rc reads the script
      """
      fn f {a <<[2=] >>[3=]}
      """
    Then whatis f prints
      """
      fn f {
      	>[2=]>[3=]a
      }
      """

  @FOG_RC_SYN_127
  Scenario: A concatenation may be a command's first word
    When rc reads the script
      """
      fn f {a^b c^d}
      """
    Then whatis f prints
      """
      fn f {
      	a^b c^d
      }
      """
