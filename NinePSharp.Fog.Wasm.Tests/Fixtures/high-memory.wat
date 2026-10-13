;; Admitted: a memory of more than 2 GiB; fd_write writes descriptor 1 what lies at 2^31.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 32769 32769)
  (data (i32.const 0x80000000) "high")
  (data (i32.const 16) "\00\00\00\80\04\00\00\00")
  (func (export "_start")
    (drop (call $fd_write (i32.const 1) (i32.const 16) (i32.const 1) (i32.const 32)))))
