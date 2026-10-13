;; Asks path_open to create "new" beneath descriptor 3 and write its descriptor past the end of
;; memory, and ends with the errno it returned.
(module
  (import "wasi_snapshot_preview1" "path_open" (func $path_open (param i32 i32 i32 i32 i32 i64 i64 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1 1)
  (data (i32.const 0) "new")
  (func (export "_start")
    (call $proc_exit (call $path_open (i32.const 3) (i32.const 0) (i32.const 0) (i32.const 3) (i32.const 1) (i64.const 0x42) (i64.const 0) (i32.const 0) (i32.const 65534)))))
