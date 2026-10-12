;; Calls a function through the table as a type it does not have.
(module
  (memory (export "memory") 1 1)
  (type $none (func))
  (func $takes (param i32))
  (table funcref (elem $takes))
  (func (export "_start")
    (call_indirect (type $none) (i32.const 0))))
