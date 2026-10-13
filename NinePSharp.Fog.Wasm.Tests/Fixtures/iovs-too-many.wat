;; Writes a vector of 131073 buffers, one more than fit in a mebibyte, all inside memory; that is
;; INVAL, 28, which _start exits with.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $exit (param i32)))
  (memory (export "memory") 32 32)
  (func (export "_start")
    (call $exit (call $fd_write (i32.const 1) (i32.const 0) (i32.const 131073) (i32.const 0x1f0000)))))
