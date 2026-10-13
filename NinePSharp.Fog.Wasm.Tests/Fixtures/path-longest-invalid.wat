;; Makes a directory beneath descriptor 3 named by a path of a mebibyte of the byte 0xff, the
;; longest one call copies; that is not UTF-8, ILSEQ, 25, which _start exits with.
(module
  (import "wasi_snapshot_preview1" "path_create_directory" (func $mkdir (param i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "proc_exit" (func $exit (param i32)))
  (memory (export "memory") 32 32)
  (func (export "_start")
    (local $at i32)
    (loop $fill
      (i32.store8 (local.get $at) (i32.const 0xff))
      (local.set $at (i32.add (local.get $at) (i32.const 1)))
      (br_if $fill (i32.lt_u (local.get $at) (i32.const 0x100000))))
    (call $exit (call $mkdir (i32.const 3) (i32.const 0) (i32.const 0x100000)))))
