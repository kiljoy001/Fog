;; Makes a directory beneath descriptor 3 named by a path of 2^31 - 1 bytes, which runs past the end
;; of memory; that is FAULT, 21, which _start exits with.
(module
  (import "wasi_snapshot_preview1" "path_create_directory" (func $mkdir (param i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $exit (param i32)))
  (memory (export "memory") 32 32)
  (func (export "_start")
    (call $exit (call $mkdir (i32.const 3) (i32.const 0) (i32.const 0x7fffffff)))))
