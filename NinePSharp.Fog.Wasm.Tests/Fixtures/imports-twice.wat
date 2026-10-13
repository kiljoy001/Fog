;; Admitted: a module that imports fd_write twice, as AssemblyScript's output does, and writes
;; "hello" to descriptor 1 through the second.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func $first (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_write" (func $second (param i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 1)
  (data (i32.const 16) "\20\00\00\00\05\00\00\00")
  (data (i32.const 32) "hello")
  (func (export "_start")
    (drop (call $second (i32.const 1) (i32.const 16) (i32.const 1) (i32.const 48)))))
