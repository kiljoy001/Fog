;; Loads from past the end of its memory.
(module
  (memory (export "memory") 1 1)
  (func (export "_start")
    (drop (i32.load (i32.const 65535)))))
