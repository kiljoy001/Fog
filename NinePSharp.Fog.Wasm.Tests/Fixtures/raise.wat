;; Calls proc_raise, and ends with the errno it returned.
(module
  (import "wasi_snapshot_preview1" "proc_raise" (func $proc_raise (param i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $proc_exit (param i32)))
  (memory (export "memory") 1 1)
  (func (export "_start")
    (call $proc_exit (call $proc_raise (i32.const 15)))))
