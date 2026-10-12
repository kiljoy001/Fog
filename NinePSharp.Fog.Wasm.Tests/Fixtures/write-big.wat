;; Writes 300 buffers of 4096 bytes to descriptor 1 in one fd_write, and ends with the count written.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1 1)
  ;; 0: written, 8: 300 iovecs, each naming the 4096 bytes at 8192
  (func (export "_start")
    (local $i i32)
    (block $done
      (loop $next
        (br_if $done (i32.eq (local.get $i) (i32.const 300)))
        (i32.store (i32.add (i32.const 8) (i32.shl (local.get $i) (i32.const 3))) (i32.const 8192))
        (i32.store (i32.add (i32.const 12) (i32.shl (local.get $i) (i32.const 3))) (i32.const 4096))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))
    (drop (call $fd_write (i32.const 1) (i32.const 8) (i32.const 300) (i32.const 0)))
    (call $proc_exit (i32.load (i32.const 0)))))
