;; Refused: a memory that may grow past 16384 pages.
(module
  (memory (export "memory") 1 16385)
  (func (export "_start")))
