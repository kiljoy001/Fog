;; Refused: an import of fd_write with the wrong type.
(module
  (import "wasi_snapshot_preview1" "fd_write" (func (param i32 i32 i32) (result i32)))
  (memory (export "memory") 1 1)
  (func (export "_start")))
