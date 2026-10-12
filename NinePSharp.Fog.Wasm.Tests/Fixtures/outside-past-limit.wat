;; Passes 256 buffers of 4096 bytes, a mebibyte, and then one that runs past the end of memory,
;; and ends with the errno fd_write returned.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1 1)
  ;; 0: written, 8: 257 iovecs, the first 256 naming the 4096 bytes at 8192
  (func (export "_start")
    (local $i i32)
    (block $done
      (loop $next
        (br_if $done (i32.eq (local.get $i) (i32.const 256)))
        (i32.store (i32.add (i32.const 8) (i32.shl (local.get $i) (i32.const 3))) (i32.const 8192))
        (i32.store (i32.add (i32.const 12) (i32.shl (local.get $i) (i32.const 3))) (i32.const 4096))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))
    (i32.store (i32.const 2056) (i32.const 65532))
    (i32.store (i32.const 2060) (i32.const 16))
    (call $proc_exit (call $fd_write (i32.const 1) (i32.const 8) (i32.const 257) (i32.const 0)))))
