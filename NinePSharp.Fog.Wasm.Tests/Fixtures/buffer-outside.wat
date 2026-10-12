;; Passes a buffer that runs past the end of memory, and ends with the errno it returned.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1 1)
  (data (i32.const 0) "\fc\ff\00\00\05\00\00\00")
  (data (i32.const 16) "hello")
  (func (export "_start")
    (call $proc_exit (call $fd_write (i32.const 1) (i32.const 0) (i32.const 1) (i32.const 8)))))
