;; Asks fd_seek to move descriptor 3 to 5 and write the new offset past the end of memory, then
;; writes the next byte descriptor 3 reads to descriptor 1, and ends with fd_seek's errno.
(module
  (import "wasi_snapshot_preview1" "fd_seek" (func $fd_seek (param i32 i64 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_read" (func $fd_read (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1 1)
  ;; 0: iovec naming the byte at 32, 16: count
  (data (i32.const 0) "\20\00\00\00\01\00\00\00")
  (func (export "_start")
    (local $errno i32)
    (local.set $errno (call $fd_seek (i32.const 3) (i64.const 5) (i32.const 0) (i32.const 65530)))
    (drop (call $fd_read (i32.const 3) (i32.const 0) (i32.const 1) (i32.const 16)))
    (drop (call $fd_write (i32.const 1) (i32.const 0) (i32.const 1) (i32.const 16)))
    (call $proc_exit (local.get $errno))))
