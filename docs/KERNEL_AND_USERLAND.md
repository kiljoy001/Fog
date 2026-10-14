# Kernel, Rc Shell & Userland Execution

This document details the kernel emulation layer, the Plan 9 command environment, and the `rc` shell compiler implementation in Fog.

---

## 1. The Fog Kernel Architecture

The Fog Kernel (`NinePSharp.Fog.Kernel`) is not a Linux-compatible POSIX layer; it is an authentic emulation of the **Plan 9 kernel interface**.

```
┌────────────────────────────────────────────────────────┐
│                   Userland (rc, cmds)                  │
└───────────────────────────┬────────────────────────────┘
                            │ System Calls
┌───────────────────────────▼────────────────────────────┐
│                       Fog Kernel                       │
├───────────────────────────┬────────────────────────────┤
│ Syscall Handlers          │ Device Table               │
│ • rfork, exec, wait       │ • #c (Console)             │
│ • open, create, close     │ • #| (Pipe)                │
│ • read, write, seek       │ • #d (Dup)                 │
│ • dup, pipe               │ • #p (Proc)                │
│ • bind, mount, unmount    │ • ramfs (In-memory FS)     │
└───────────────────────────┴────────────────────────────┘
```

### System Call Interface
Programs interact with the kernel using asynchronous C# APIs that match Plan 9 syscall signatures:

* `rfork(RforkFlags flags)`: Creates a child process. Flags determine whether the child shares or duplicates the parent's file descriptor table (`RFFDG`), namespace mount table (`RFNAMEG`), environment (`RFENVG`), or runs detached without wait requirements (`RFNOWAIT`).
* `exec(string name, string[] argv)`: Replaces the current process program with a new binary or WASM application.
* `bind(string name, string old, int flag)`: Rebinds a file or directory into a namespace. Flags support `MREPL` (replace), `MBEFORE` (union before), and `MAFTER` (union after).
* `mount(int fd, int afd, string old, int flag, string aname)`: Attaches a remote or in-process 9P file server into the current namespace hierarchy.

---

## 2. Kernel Devices

Plan 9 represents system capabilities as special device files prefixed with `#`:

### `#c` — Console Device ([`ConsDevice`](../NinePSharp.Fog.Kernel/ConsDevice.cs))
* Provides `/dev/cons` for standard input and output.
* Supports raw and cooked input modes, keyboard escape sequences, and status querying.

### `#|` — Pipe Device ([`PipeDevice`](../NinePSharp.Fog.Kernel/PipeDevice.cs))
* Creates pairs of connected descriptors `/dev/data` and `/dev/data1`.
* Writing to one descriptor delivers bytes to the other.
* Maintains byte-size queue limits and enforces proper EOF behavior when the write end clunks.

### `#d` — Dup Device ([`DupDevice`](../NinePSharp.Fog.Kernel/DupDevice.cs))
* Exposes numbered descriptors as files (e.g., `/dev/fd/0`, `/dev/fd/1`).
* Opening `/dev/fd/N` clones file descriptor $N$.

### `#p` — Process Device ([`ProcessDevices`](../NinePSharp.Fog.Kernel/ProcessDevices.cs))
* Exposes active processes under `/proc/{pid}/`.
* Allows inspection of process status, memory stats, and descriptor tables.

---

## 3. The `rc` Shell Port (`NinePSharp.Fog.Rc`)

Fog includes a complete managed implementation of **Tom Duff's `rc` shell**, ported from 9front.

### Lexing & Parsing Pipeline
1. **Lexer ([`RcLexer`](../NinePSharp.Fog.Rc/RcLexer.cs)):** Tokenizes `rc` syntax, handling list literals (`(a b c)`), quote escaping (`'it''s'`), and control characters.
2. **Parser ([`RcParser`](../NinePSharp.Fog.Rc/RcParser.cs)):** Generates AST nodes (`RcTree`) representing commands, assignments, redirections, and pipelines.
3. **Compiler ([`RcCompiler`](../NinePSharp.Fog.Rc/RcCompiler.cs)):** Emits flat bytecode instructions (`RcInstruction`) with jump targets and variable lookups.

### Supported `rc` Constructs
* **List Processing:** Native lists as the primary data type:
  ```rc
  fn greeting { echo hello $1 }
  greeting world
  ```
* **Pipelines & Redirections:**
  ```rc
  cat < /env/user | tr a-z A-Z > /tmp/upper
  ```
* **Control Flow:**
  ```rc
  if(~ $#* 0){
      echo 'no arguments given'
  }
  for(i in a b c){
      echo $i
  }
  ```

---

## 4. Concurrency with `libthread` (`NinePSharp.Fog.Thread`)

Processes and services in Fog achieve concurrency using Plan 9's **libthread** Communicating Sequential Processes (CSP) model rather than raw OS mutexes:

* **Channels ([`Channel<T>`](../NinePSharp.Fog.Thread/Channel%7BT%7D.cs)):** Typed channels that can be buffered or unbuffered.
* **`Alt` Multiplexing ([`Alt<T>`](../NinePSharp.Fog.Thread/Alt%7BT%7D.cs)):** Allows a process to wait across multiple channels simultaneously:
  ```csharp
  var alt = new Alt<string>(new[] {
      channelA.ReceiveOp(),
      channelB.SendOp("ready")
  });
  int selectedIndex = await alt.ExecuteAsync();
  ```
* **Deterministic Verification:** Every channel and alt operation is tested under **Microsoft Coyote** to ensure that thread interleavings cannot deadlock or leak state.
