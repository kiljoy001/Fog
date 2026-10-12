;; Calls through a table entry that does not exist.
(module
  (memory (export "memory") 1 1)
  (type $none (func))
  (table 1 funcref)
  (func (export "_start")
    (call_indirect (type $none) (i32.const 5))))
