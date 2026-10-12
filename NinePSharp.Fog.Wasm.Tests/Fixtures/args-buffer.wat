;; Writes the whole buffer args_get fills, NULs and all, to descriptor 1.
(module
  (import "wasi_snapshot_preview1" "args_sizes_get" (func $sizes (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "args_get" (func $get (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 1 1)
  ;; 0: count, 4: buffer size, 8: iovec, 16: written, 64: pointers, 4096: strings
  (func (export "_start")
    (drop (call $sizes (i32.const 0) (i32.const 4)))
    (drop (call $get (i32.const 64) (i32.const 4096)))
    (i32.store (i32.const 8) (i32.const 4096))
    (i32.store (i32.const 12) (i32.load (i32.const 4)))
    (drop (call $fd_write (i32.const 1) (i32.const 8) (i32.const 1) (i32.const 16)))))
