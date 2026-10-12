;; Refused: an import of proc_exit with a result.
(module
  (import "wasi_snapshot_preview1" "proc_exit" (func (param i32) (result i32)))
  (memory (export "memory") 1 1)
  (func (export "_start")))
