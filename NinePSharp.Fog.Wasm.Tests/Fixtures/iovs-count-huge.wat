;; Writes a vector of 2^31 buffers, far more than memory holds; the vector lies outside memory, FAULT,
;; 21, which _start exits with.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $exit (param i32)))
  (memory (export "memory") 32 32)
  (func (export "_start")
    (call $exit (call $fd_write (i32.const 1) (i32.const 0) (i32.const 0x80000000) (i32.const 0x1f0000)))))
