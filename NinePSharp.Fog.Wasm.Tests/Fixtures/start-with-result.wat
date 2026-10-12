;; Refused: a _start that returns a value.
(module
  (memory (export "memory") 1 1)
  (func (export "_start") (result i32)
    (i32.const 0)))
