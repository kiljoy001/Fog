;; Refused: an imported global.
(module
  (import "env" "g" (global i32))
  (memory (export "memory") 1 1)
  (func (export "_start")))
