;; Refused: a start section.
(module
  (memory (export "memory") 1 1)
  (func $init)
  (start $init)
  (func (export "_start")))
