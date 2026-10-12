;; Ends with the size of the buffer args_sizes_get asks for.
(module
  (import "wasi_snapshot_preview1" "args_sizes_get" (func $sizes (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "args_get" (func $get (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1 1)
  (func (export "_start")
    (drop (call $sizes (i32.const 0) (i32.const 4)))
    (call $proc_exit (i32.load (i32.const 4)))))
