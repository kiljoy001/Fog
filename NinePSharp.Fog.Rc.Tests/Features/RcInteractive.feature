@fog_rc_interactive
Feature: rc reads interactively as 9front rc -i does
  Read with -i, rc prompts with the first element of $prompt before each command and with the second
  before each continued line and each line of a here document, as pprompt does, and a syntax error
  does not end reading. Input is standard input, named /fd/0 in messages. These scenarios use
  prompt=('%' '+'). The expected text below was recorded from 9front's rc; with FOG_9FRONT_ISO, every
  scenario is also run through 9front's rc and must still match.

  @FOG_RC_INT_001
  Scenario: After a syntax error at a word, reading goes on at the next line
    When rc -i reads the script
      """
      {a} b
      fn f {c}
      whatis f
      """
    Then it prints
      """
      %/fd/0:1: token b: syntax error
      %%fn f {
      	c
      }
      %
      """

  @FOG_RC_INT_002
  Scenario: After a syntax error at $, the next word is not a variable name
    When rc -i reads the script
      """
      {a} $
      fn f {x.y}
      whatis f
      """
    Then it prints
      """
      %/fd/0:1: token '$': syntax error
      %%fn f {
      	x.y
      }
      %
      """

  @FOG_RC_INT_003
  Scenario: After a redirection error, the line counter has passed the skipped newline
    When rc -i reads the script
      """
      echo >[x
      fn f {a}
      whatis f
      """
    Then it prints
      """
      %/fd/0:1: token '>[x': redirection syntax
      /fd/0:2: token '>[x': syntax error
      %%fn f {
      	a
      }
      %
      """

  @FOG_RC_INT_004
  Scenario: A continued line is prompted with the second prompt
    When rc -i reads the script
      """
      fn f {a \
      b
      }
      whatis f
      """
    Then it prints
      """
      %++%fn f {
      	a b
      }
      %
      """

  @FOG_RC_INT_005
  Scenario: Each line of a here document is prompted
    When rc -i reads the script
      """
      fn f {cat <<X
      line
      X
      }
      whatis f
      """
    Then it prints
      """
      %+++%fn f {
      	 cat <<X
      line
      X

      }
      %
      """

  @FOG_RC_INT_006
  Scenario: A comment line is a line of its own
    When rc -i reads the script
      """
      # x
      fn f {a}
      whatis f
      """
    Then it prints
      """
      %%%fn f {
      	a
      }
      %
      """
