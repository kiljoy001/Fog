;; Admitted: a memory without a maximum, which may grow to WebAssembly's limit for a 32-bit memory
;; and no further: growing by 65536 pages from one fails, and _start traps unless it does.
(module
  (memory (export "memory") 1)
  (func (export "_start")
    (if (i32.ne (memory.grow (i32.const 65536)) (i32.const -1))
      (then unreachable))))
