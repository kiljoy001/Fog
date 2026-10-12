;; Calls through a table entry that holds no function.
(module
  (memory (export "memory") 1 1)
  (type $none (func))
  (table 2 funcref)
  (func (export "_start")
    (call_indirect (type $none) (i32.const 1))))
