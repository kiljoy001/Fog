;; Divides by zero.
(module
  (memory (export "memory") 1 1)
  (global $zero (mut i32) (i32.const 0))
  (func (export "_start")
    (drop (i32.div_u (i32.const 1) (global.get $zero)))))
