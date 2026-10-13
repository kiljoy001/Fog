;; Polls 21846 subscriptions, one more than fit in a mebibyte, all inside memory; that is INVAL,
;; 28, which _start exits with.
(module
  (import "wasi_snapshot_preview1" "poll_oneoff" (func $poll (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $exit (param i32)))
  (memory (export "memory") 64 64)
  (func (export "_start")
    (call $exit (call $poll (i32.const 0) (i32.const 0x200000) (i32.const 21846) (i32.const 0x3f0000)))))
