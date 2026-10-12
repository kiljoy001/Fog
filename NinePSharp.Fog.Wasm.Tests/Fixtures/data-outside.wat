;; Refused: data that runs past the end of its memory.
(module
  (memory (export "memory") 1 1)
  (data (i32.const 65535) "ab")
  (func (export "_start")))
