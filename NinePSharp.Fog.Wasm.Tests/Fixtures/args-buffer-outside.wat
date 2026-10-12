;; Asks args_get to write the strings past the end of memory, and ends with the errno it returned.
(module
  (import "wasi_snapshot_preview1" "args_sizes_get" (func $sizes (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "args_get" (func $get (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1 1)
  (func (export "_start")
    (call $proc_exit (call $get (i32.const 0) (i32.const 65526)))))
