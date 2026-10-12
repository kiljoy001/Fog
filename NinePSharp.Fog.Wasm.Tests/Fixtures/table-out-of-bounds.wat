;; Reads a table entry past the end of the table.
(module
  (memory (export "memory") 1 1)
  (table 1 funcref)
  (func (export "_start")
    (drop (table.get 0 (i32.const 5)))))
