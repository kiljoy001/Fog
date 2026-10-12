;; Executes unreachable.
(module
  (memory (export "memory") 1 1)
  (func (export "_start")
    unreachable))
