;; Asks fd_read to write the count past the end of memory, and ends with the errno it returned.
(module
  (import "wasi_snapshot_preview1" "fd_read" (func $fd_read (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1 1)
  (data (i32.const 0) "\10\00\00\00\08\00\00\00")
  (func (export "_start")
    (call $proc_exit (call $fd_read (i32.const 1) (i32.const 0) (i32.const 1) (i32.const 65534)))))
