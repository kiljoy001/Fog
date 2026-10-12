;; Divides the smallest i32 by -1.
(module
  (memory (export "memory") 1 1)
  (global $min (mut i32) (i32.const 0x80000000))
  (func (export "_start")
    (drop (i32.div_s (global.get $min) (i32.const -1)))))
