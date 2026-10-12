;; Refused: an import from a module other than Preview 1.
(module
  (import "env" "print" (func (param i32)))
  (memory (export "memory") 1 1)
  (func (export "_start")))
