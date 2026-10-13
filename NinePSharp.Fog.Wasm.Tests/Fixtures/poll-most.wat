;; Polls 21845 subscriptions, the most whose 48 bytes each fit in a mebibyte; zeroed, each is a
;; relative realtime clock already due. _start exits with the errno, 0.
(module
  (import "wasi_snapshot_preview1" "poll_oneoff" (func $poll (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $exit (param i32)))
  (memory (export "memory") 64 64)
  (func (export "_start")
    (call $exit (call $poll (i32.const 0) (i32.const 0x200000) (i32.const 21845) (i32.const 0x3f0000)))))
