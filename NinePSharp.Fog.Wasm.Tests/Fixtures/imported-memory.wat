;; Refused: an imported memory.
(module
  (import "env" "memory" (memory 1 1))
  (export "memory" (memory 0))
  (func (export "_start")))
