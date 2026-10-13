;; Admitted: random_get of 2 GiB inside a memory of more; one access that long is FAULT, 21, which
;; _start exits with.
(module
  (import "wasi_snapshot_preview1" "random_get" (func $random (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $exit (param i32)))
  (memory (export "memory") 32769 32769)
  (func (export "_start")
    (call $exit (call $random (i32.const 0) (i32.const 0x80000000)))))
