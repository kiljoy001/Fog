;; Refused: elements that run past the end of its table.
(module
  (memory (export "memory") 1 1)
  (table 1 funcref)
  (func $f)
  (elem (i32.const 1) $f)
  (func (export "_start")))
