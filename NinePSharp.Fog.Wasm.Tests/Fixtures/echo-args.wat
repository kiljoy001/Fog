;; Writes each of its args strings to descriptor 1, each followed by a newline.
(module
  (import "wasi_snapshot_preview1" "args_sizes_get" (func $sizes (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "args_get" (func $get (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 1 1)
  ;; 0: count, 4: buffer size, 8: iovec, 16: written, 64: pointers, 4096: strings
  (func (export "_start")
    (local $i i32) (local $p i32) (local $n i32)
    (drop (call $sizes (i32.const 0) (i32.const 4)))
    (drop (call $get (i32.const 64) (i32.const 4096)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (i32.load (i32.const 0))))
        (local.set $p (i32.load (i32.add (i32.const 64) (i32.shl (local.get $i) (i32.const 2)))))
        (local.set $n (i32.const 0))
        (block $end
          (loop $scan
            (br_if $end (i32.eqz (i32.load8_u (i32.add (local.get $p) (local.get $n)))))
            (local.set $n (i32.add (local.get $n) (i32.const 1)))
            (br $scan)))
        (i32.store8 (i32.add (local.get $p) (local.get $n)) (i32.const 10))
        (i32.store (i32.const 8) (local.get $p))
        (i32.store (i32.const 12) (i32.add (local.get $n) (i32.const 1)))
        (drop (call $fd_write (i32.const 1) (i32.const 8) (i32.const 1) (i32.const 16)))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))))
