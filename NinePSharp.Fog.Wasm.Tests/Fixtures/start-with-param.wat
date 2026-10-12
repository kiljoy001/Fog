;; Refused: a _start that takes a parameter.
(module
  (memory (export "memory") 1 1)
  (func (export "_start") (param i32)))
