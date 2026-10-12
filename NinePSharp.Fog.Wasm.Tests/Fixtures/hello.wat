;; Writes "hello" to descriptor 1. A second vector entry follows the one it passes, so reading past
;; the vector would write "hello" twice.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 1 1)
  (data (i32.const 0) "\20\00\00\00\05\00\00\00\20\00\00\00\05\00\00\00")
  (data (i32.const 32) "hello")
  (func (export "_start")
    (drop (call $fd_write (i32.const 1) (i32.const 0) (i32.const 1) (i32.const 16)))))
