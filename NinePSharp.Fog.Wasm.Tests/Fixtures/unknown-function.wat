;; Refused: an import of a function Preview 1 does not have.
(module
  (import "wasi_snapshot_preview1" "fd_nonesuch" (func (param i32) (result i32)))
  (memory (export "memory") 1 1)
  (func (export "_start")))
