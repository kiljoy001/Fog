# Stryker timeouts

The mutants Stryker reports as timeouts in the Fog kernel, commands and rc runs. Each one makes a
loop stop advancing, so the interpreter or kernel keeps the CPU busy until Stryker gives up. None of
them waits on a descriptor, a child or a lock: the tests read with bounded waits, so a mutant that
blocked would fail its test rather than time out.

Recorded from the runs of 2026-10-03, on the commits up to 861cef9. Line numbers are those of that
tree. To list them again after a run:

```bash
jq -r '.files | to_entries[] | .key as $f | .value.mutants[] | select(.status=="Timeout")
  | "\($f|split("/")|last):\(.location.start.line) \(.mutatorName) => \(.replacement)"' \
  .artifacts/stryker-fog-rc/reports/mutation-report.json
```

## NinePSharp.Fog.Commands

None.

## NinePSharp.Fog.Kernel (14)

| Where | Mutation | Why it loops |
|---|---|---|
| Errors.cs:33 | `2 / ErrMax`; `&&` to `\|\|`; the condition negated | namelenerror trims the path in a loop that no longer reaches its end |
| Errors.cs:40 | `next < 0`; `&&` to `\|\|`; the condition negated | the backward search for `/` never stops |
| Errors.cs:44 | `ErrMax * 3`; `2 * ErrMax * 3`; `- error.Length`; `<=` and `>` for `<` | the do-while trimming the name never meets its bound |
| PipeQueue.cs:73 | `sofar <= data.Length` | a write of nothing left keeps writing empty blocks |
| Process.cs:266 | `entries.Add(new Stat(...))` removed | the directory read never advances its offset |

## NinePSharp.Fog.Rc (36)

### Lexer and parser (24)

| Where | Mutation | Why it loops |
|---|---|---|
| RcLexer.cs:121, 124, 189, 596, 602 | the advance or return removed | the lexer reads the same character forever |
| RcLexer.cs:185 | `LastWord = false` to `true` | the lexer stops advancing through its input |
| RcLexer.cs:220 | `WordChar`'s `&&` to `\|\|` | end of file counts as a word character |
| RcLexer.cs:281 (2), 283 | the word's end test negated; its `break` removed | a word never ends |
| RcLexer.cs:449, 593 | a `break` removed | a scanning loop never leaves |
| RcLexer.cs:515 (3) | the backslash test changed | the lexer stops advancing through its input |
| RcLexer.cs:600, 606 | the blank test negated; a `return` removed | skipping blanks never stops |
| RcParser.cs:59 (2) | `while (true)` made false or negated | the parse loop restarts without consuming input |
| RcParser.cs:61, 71, 109, 153, 312 | the stack, action table or reduction changed | the automaton cycles between states |

### Compiler and interpreter (12)

| Where | Mutation | Why it loops |
|---|---|---|
| RcCompiler.cs:339 | a `while`'s condition not compiled | an empty condition is `while(true)` |
| RcCompiler.cs:348 | a `while`'s body not compiled | the loop runs with nothing to end it |
| RcCompiler.cs:511 | `t.Type != '='` | the walk down a chain of assignments never ends |
| RcInput.cs:9 | end of input `-1` to `+1` | the test input source never reaches end of file |
| RcShell.cs:55 | `Clobbers` returns true | every redirection is moved, again and again |
| RcShell.cs:278 (2) | the clobber test made `\|\|` or negated | pushredir moves descriptors without end |
| RcShell.cs:315 | `r.Next == rp.Next` | shuffleredir's walk never finds its end |
| RcShell.Ops.cs:245 | `trapped = true` to `false` | sigexit calls itself as it exits |
| RcShell.Ops.cs:251 | `&&` to `\|\|` | sigexit runs again for every exit |
| RcShell.Simple.cs:369, 682 | shift's or rfork's `Poplist` removed | the arguments left behind become a `for` loop's list, one more each time round |
