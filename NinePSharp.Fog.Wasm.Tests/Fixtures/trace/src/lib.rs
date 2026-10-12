//! The WASI Preview 1 trace interpreter: reads a script of calls on stdin, makes each call directly
//! on wasi_snapshot_preview1 (no libc between), and writes one trace line per call on stdout: the
//! call as written, then "-> ok" and its results, or "->" and the errno's name.
//!
//! A script line is a function's WITX name and its arguments. Numbers are decimal (negative ones
//! too) or 0x hex; strings are double-quoted, with \n, \t, \0, \\, \" and \xHH escapes. Whence may
//! be set, cur or end. Blank lines and lines starting with # are skipped and not traced.

#![no_std]

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! {
    core::arch::wasm32::unreachable()
}

#[link(wasm_import_module = "wasi_snapshot_preview1")]
extern "C" {
    fn args_get(argv: i32, buf: i32) -> i32;
    fn args_sizes_get(count: i32, size: i32) -> i32;
    fn environ_get(environ: i32, buf: i32) -> i32;
    fn environ_sizes_get(count: i32, size: i32) -> i32;
    fn clock_res_get(id: i32, res: i32) -> i32;
    fn clock_time_get(id: i32, precision: i64, time: i32) -> i32;
    fn fd_advise(fd: i32, offset: i64, len: i64, advice: i32) -> i32;
    fn fd_allocate(fd: i32, offset: i64, len: i64) -> i32;
    fn fd_close(fd: i32) -> i32;
    fn fd_datasync(fd: i32) -> i32;
    fn fd_fdstat_get(fd: i32, stat: i32) -> i32;
    fn fd_fdstat_set_flags(fd: i32, flags: i32) -> i32;
    fn fd_fdstat_set_rights(fd: i32, base: i64, inheriting: i64) -> i32;
    fn fd_filestat_get(fd: i32, stat: i32) -> i32;
    fn fd_filestat_set_size(fd: i32, size: i64) -> i32;
    fn fd_filestat_set_times(fd: i32, atim: i64, mtim: i64, flags: i32) -> i32;
    fn fd_pread(fd: i32, iovs: i32, len: i32, offset: i64, nread: i32) -> i32;
    fn fd_prestat_get(fd: i32, prestat: i32) -> i32;
    fn fd_prestat_dir_name(fd: i32, path: i32, len: i32) -> i32;
    fn fd_pwrite(fd: i32, iovs: i32, len: i32, offset: i64, nwritten: i32) -> i32;
    fn fd_read(fd: i32, iovs: i32, len: i32, nread: i32) -> i32;
    fn fd_readdir(fd: i32, buf: i32, len: i32, cookie: i64, used: i32) -> i32;
    fn fd_renumber(fd: i32, to: i32) -> i32;
    fn fd_seek(fd: i32, offset: i64, whence: i32, newoffset: i32) -> i32;
    fn fd_sync(fd: i32) -> i32;
    fn fd_tell(fd: i32, offset: i32) -> i32;
    fn fd_write(fd: i32, iovs: i32, len: i32, nwritten: i32) -> i32;
    fn path_create_directory(fd: i32, path: i32, len: i32) -> i32;
    fn path_filestat_get(fd: i32, flags: i32, path: i32, len: i32, stat: i32) -> i32;
    fn path_filestat_set_times(fd: i32, flags: i32, path: i32, len: i32, atim: i64, mtim: i64, fst: i32) -> i32;
    fn path_link(oldfd: i32, flags: i32, old: i32, oldlen: i32, newfd: i32, new: i32, newlen: i32) -> i32;
    fn path_open(fd: i32, dirflags: i32, path: i32, len: i32, oflags: i32, base: i64, inheriting: i64, fdflags: i32, opened: i32) -> i32;
    fn path_readlink(fd: i32, path: i32, len: i32, buf: i32, buflen: i32, used: i32) -> i32;
    fn path_remove_directory(fd: i32, path: i32, len: i32) -> i32;
    fn path_rename(fd: i32, old: i32, oldlen: i32, newfd: i32, new: i32, newlen: i32) -> i32;
    fn path_symlink(old: i32, oldlen: i32, fd: i32, new: i32, newlen: i32) -> i32;
    fn path_unlink_file(fd: i32, path: i32, len: i32) -> i32;
    fn poll_oneoff(subs: i32, events: i32, count: i32, nevents: i32) -> i32;
    fn proc_exit(code: i32);
    fn proc_raise(sig: i32) -> i32;
    fn sched_yield() -> i32;
    fn random_get(buf: i32, len: i32) -> i32;
    fn sock_accept(fd: i32, flags: i32, opened: i32) -> i32;
    fn sock_recv(fd: i32, iovs: i32, len: i32, flags: i32, nread: i32, oflags: i32) -> i32;
    fn sock_send(fd: i32, iovs: i32, len: i32, flags: i32, nwritten: i32) -> i32;
    fn sock_shutdown(fd: i32, how: i32) -> i32;
}

const ERRNOS: [&str; 77] = [
    "SUCCESS", "2BIG", "ACCES", "ADDRINUSE", "ADDRNOTAVAIL", "AFNOSUPPORT", "AGAIN", "ALREADY", "BADF", "BADMSG",
    "BUSY", "CANCELED", "CHILD", "CONNABORTED", "CONNREFUSED", "CONNRESET", "DEADLK", "DESTADDRREQ", "DOM", "DQUOT",
    "EXIST", "FAULT", "FBIG", "HOSTUNREACH", "IDRM", "ILSEQ", "INPROGRESS", "INTR", "INVAL", "IO", "ISCONN", "ISDIR",
    "LOOP", "MFILE", "MLINK", "MSGSIZE", "MULTIHOP", "NAMETOOLONG", "NETDOWN", "NETRESET", "NETUNREACH", "NFILE",
    "NOBUFS", "NODEV", "NOENT", "NOEXEC", "NOLCK", "NOLINK", "NOMEM", "NOMSG", "NOPROTOOPT", "NOSPC", "NOSYS",
    "NOTCONN", "NOTDIR", "NOTEMPTY", "NOTRECOVERABLE", "NOTSOCK", "NOTSUP", "NOTTY", "NXIO", "OVERFLOW", "OWNERDEAD",
    "PERM", "PIPE", "PROTO", "PROTONOSUPPORT", "PROTOTYPE", "RANGE", "ROFS", "SPIPE", "SRCH", "STALE", "TIMEDOUT",
    "TXTBSY", "XDEV", "NOTCAPABLE",
];

// The script, the trace line being built, and where arguments and results are placed for a call.
static mut INPUT: [u8; 65536] = [0; 65536];
static mut OUT: [u8; 65536] = [0; 65536];
static mut OUT_LEN: usize = 0;
static mut ARENA: [u8; 1 << 20] = [0; 1 << 20];
static mut ARENA_LEN: usize = 0;

fn arena() -> &'static mut [u8; 1 << 20] {
    unsafe { &mut *core::ptr::addr_of_mut!(ARENA) }
}

// Room for n bytes in the arena, 8-aligned, as a guest pointer.
fn alloc(n: usize) -> i32 {
    unsafe {
        let at = (ARENA_LEN + 7) & !7;
        ARENA_LEN = at + n;
        arena().as_ptr().add(at) as i32
    }
}

fn mem(ptr: i32, len: usize) -> &'static mut [u8] {
    unsafe { core::slice::from_raw_parts_mut(ptr as *mut u8, len) }
}

fn put_u32(ptr: i32, v: u32) {
    mem(ptr, 4).copy_from_slice(&v.to_le_bytes());
}

fn get_u32(ptr: i32) -> u32 {
    u32::from_le_bytes(mem(ptr, 4).try_into().unwrap())
}

fn get_u64(ptr: i32) -> u64 {
    u64::from_le_bytes(mem(ptr, 8).try_into().unwrap())
}

fn emit(bytes: &[u8]) {
    unsafe {
        let out = &mut *core::ptr::addr_of_mut!(OUT);
        let n = bytes.len().min(out.len() - OUT_LEN);
        out[OUT_LEN..OUT_LEN + n].copy_from_slice(&bytes[..n]);
        OUT_LEN += n;
    }
}

fn emit_u64(mut v: u64) {
    let mut digits = [0u8; 20];
    let mut i = digits.len();
    loop {
        i -= 1;
        digits[i] = b'0' + (v % 10) as u8;
        v /= 10;
        if v == 0 {
            break;
        }
    }
    emit(&digits[i..]);
}

fn emit_hex(v: u64) {
    emit(b"0x");
    let mut started = false;
    for shift in (0..16).rev() {
        let d = ((v >> (shift * 4)) & 0xf) as u8;
        if d != 0 || started || shift == 0 {
            started = true;
            emit(&[if d < 10 { b'0' + d } else { b'a' + d - 10 }]);
        }
    }
}

fn emit_quoted(bytes: &[u8]) {
    emit(b"\"");
    for &b in bytes {
        match b {
            b'\n' => emit(b"\\n"),
            b'\t' => emit(b"\\t"),
            b'\\' => emit(b"\\\\"),
            b'"' => emit(b"\\\""),
            0x20..=0x7e => emit(&[b]),
            _ => {
                let hex = b"0123456789abcdef";
                emit(&[b'\\', b'x', hex[(b >> 4) as usize], hex[(b & 0xf) as usize]]);
            }
        }
    }
    emit(b"\"");
}

fn flush() {
    unsafe {
        let iov = alloc(8);
        put_u32(iov, core::ptr::addr_of!(OUT) as u32);
        put_u32(iov + 4, OUT_LEN as u32);
        let written = alloc(4);
        fd_write(1, iov, 1, written);
        OUT_LEN = 0;
    }
}

// The arguments of a script line, read one at a time.
struct Args<'a> {
    rest: &'a [u8],
}

impl<'a> Args<'a> {
    fn token(&mut self) -> Option<&'a [u8]> {
        while let [b' ', tail @ ..] = self.rest {
            self.rest = tail;
        }
        if self.rest.is_empty() {
            return None;
        }
        let mut end = 0;
        let mut quoted = false;
        while end < self.rest.len() && (quoted || self.rest[end] != b' ') {
            if self.rest[end] == b'\\' && quoted {
                end += 1;
            } else if self.rest[end] == b'"' {
                quoted = !quoted;
            }
            end += 1;
        }
        let token = &self.rest[..end.min(self.rest.len())];
        self.rest = &self.rest[end.min(self.rest.len())..];
        Some(token)
    }

    fn int(&mut self) -> i64 {
        let t = self.token().unwrap_or(b"0");
        match t {
            b"set" => return 0,
            b"cur" => return 1,
            b"end" => return 2,
            _ => {}
        }
        let (negative, digits) = match t {
            [b'-', d @ ..] => (true, d),
            d => (false, d),
        };
        let (radix, digits) = match digits {
            [b'0', b'x', d @ ..] => (16, d),
            d => (10, d),
        };
        let mut v: u64 = 0;
        for &c in digits {
            let d = match c {
                b'0'..=b'9' => c - b'0',
                b'a'..=b'f' => c - b'a' + 10,
                _ => 0,
            };
            v = v.wrapping_mul(radix).wrapping_add(d as u64);
        }
        if negative {
            (v as i64).wrapping_neg()
        } else {
            v as i64
        }
    }

    fn i32(&mut self) -> i32 {
        self.int() as i32
    }

    // A quoted string, unescaped into the arena: its pointer and length.
    fn string(&mut self) -> (i32, i32) {
        let t = self.token().unwrap_or(b"\"\"");
        let body = if t.len() >= 2 { &t[1..t.len() - 1] } else { &t[..0] };
        let at = alloc(body.len());
        let out = mem(at, body.len());
        let (mut i, mut n) = (0, 0);
        while i < body.len() {
            let b = if body[i] == b'\\' && i + 1 < body.len() {
                i += 1;
                match body[i] {
                    b'n' => b'\n',
                    b't' => b'\t',
                    b'0' => 0,
                    b'x' if i + 2 < body.len() => {
                        let h = |c: u8| if c.is_ascii_digit() { c - b'0' } else { (c | 0x20) - b'a' + 10 };
                        i += 2;
                        h(body[i - 1]) << 4 | h(body[i])
                    }
                    c => c,
                }
            } else {
                body[i]
            };
            out[n] = b;
            n += 1;
            i += 1;
        }
        (at, n as i32)
    }

    // The quoted strings left on the line, as an iovec array: its pointer and count.
    fn iovecs(&mut self) -> (i32, i32) {
        let mut parts = [(0i32, 0i32); 16];
        let mut count = 0;
        while count < parts.len() && self.rest.iter().any(|&b| b != b' ') {
            parts[count] = self.string();
            count += 1;
        }
        let iovs = alloc(8 * count);
        for (i, (ptr, len)) in parts[..count].iter().enumerate() {
            put_u32(iovs + 8 * i as i32, *ptr as u32);
            put_u32(iovs + 8 * i as i32 + 4, *len as u32);
        }
        (iovs, count as i32)
    }

    // Buffers of the sizes left on the line, as an iovec array: its pointer and count.
    fn buffers(&mut self) -> (i32, i32) {
        let mut sizes = [0usize; 16];
        let mut count = 0;
        while count < sizes.len() && self.rest.iter().any(|&b| b != b' ') {
            sizes[count] = self.int() as usize;
            count += 1;
        }
        let iovs = alloc(8 * count);
        for (i, size) in sizes[..count].iter().enumerate() {
            put_u32(iovs + 8 * i as i32, alloc(*size) as u32);
            put_u32(iovs + 8 * i as i32 + 4, *size as u32);
        }
        (iovs, count as i32)
    }
}

// What a successful call reports after "-> ok".
fn emit_gathered(iovs: i32, count: i32, n: u32) {
    emit(b" ");
    let mut left = n as usize;
    let mut bytes = [0u8; 4096];
    let mut len = 0;
    for i in 0..count {
        let ptr = get_u32(iovs + 8 * i) as i32;
        let size = (get_u32(iovs + 8 * i + 4) as usize).min(left);
        let take = size.min(bytes.len() - len);
        bytes[len..len + take].copy_from_slice(mem(ptr, take));
        len += take;
        left -= size;
    }
    emit_quoted(&bytes[..len]);
}

fn emit_strings(get: unsafe extern "C" fn(i32, i32) -> i32, sizes: unsafe extern "C" fn(i32, i32) -> i32) -> i32 {
    let counts = alloc(8);
    let e = unsafe { sizes(counts, counts + 4) };
    if e != 0 {
        return e;
    }
    let count = get_u32(counts) as usize;
    let list = alloc(4 * count);
    let buf = alloc(get_u32(counts + 4) as usize);
    let e = unsafe { get(list, buf) };
    if e == 0 {
        emit(b" ok");
        for i in 0..count {
            let s = get_u32(list + 4 * i as i32) as i32;
            let mut n = 0;
            while mem(s + n, 1)[0] != 0 {
                n += 1;
            }
            emit(b" ");
            emit_quoted(mem(s, n as usize));
        }
    }
    e
}

fn emit_filestat(st: i32) {
    emit(b" ok dev=");
    emit_u64(get_u64(st));
    emit(b" ino=");
    emit_u64(get_u64(st + 8));
    emit(b" filetype=");
    emit_u64(mem(st + 16, 1)[0] as u64);
    emit(b" nlink=");
    emit_u64(get_u64(st + 24));
    emit(b" size=");
    emit_u64(get_u64(st + 32));
    emit(b" atim=");
    emit_u64(get_u64(st + 40));
    emit(b" mtim=");
    emit_u64(get_u64(st + 48));
    emit(b" ctim=");
    emit_u64(get_u64(st + 56));
}

fn emit_dirents(buf: i32, used: u32) {
    let mut at = 0u32;
    while at + 24 <= used {
        let next = get_u64(buf + at as i32);
        let namlen = get_u32(buf + at as i32 + 16);
        let filetype = mem(buf + at as i32 + 20, 1)[0];
        if at + 24 + namlen > used {
            emit(b" partial");
            return;
        }
        emit(b" ");
        emit_quoted(mem(buf + at as i32 + 24, namlen as usize));
        emit(b":");
        emit_u64(filetype as u64);
        emit(b":");
        emit_u64(next);
        at += 24 + namlen;
    }
    if at < used {
        emit(b" partial");
    }
}

// poll_oneoff's subscriptions: "clock ID TIMEOUT FLAGS", "read FD" or "write FD", each with its
// index as userdata.
fn poll(a: &mut Args) -> i32 {
    let subs = alloc(48 * 16);
    let mut count = 0;
    while let Some(kind) = a.token() {
        let s = subs + 48 * count;
        mem(s, 48).fill(0);
        mem(s, 8).copy_from_slice(&(count as u64).to_le_bytes());
        match kind {
            b"clock" => {
                mem(s + 8, 1)[0] = 0;
                put_u32(s + 16, a.i32() as u32);
                mem(s + 24, 8).copy_from_slice(&(a.int() as u64).to_le_bytes());
                mem(s + 40, 2).copy_from_slice(&(a.i32() as u16).to_le_bytes());
            }
            b"read" | b"write" => {
                mem(s + 8, 1)[0] = if kind == b"read" { 1 } else { 2 };
                put_u32(s + 16, a.i32() as u32);
            }
            _ => mem(s + 8, 1)[0] = 9,
        }
        count += 1;
    }
    let events = alloc(32 * 16);
    let n = alloc(4);
    let e = unsafe { poll_oneoff(subs, events, count, n) };
    if e == 0 {
        emit(b" ok");
        for i in 0..get_u32(n) as i32 {
            let ev = events + 32 * i;
            emit(b" (");
            emit_u64(get_u64(ev));
            emit(b" ");
            emit(ERRNOS.get(u16::from_le_bytes([mem(ev + 8, 1)[0], mem(ev + 9, 1)[0]]) as usize).copied().unwrap_or("?").as_bytes());
            emit(b" ");
            emit_u64(mem(ev + 10, 1)[0] as u64);
            emit(b" ");
            emit_u64(get_u64(ev + 16));
            emit(b" ");
            emit_u64(u16::from_le_bytes([mem(ev + 24, 1)[0], mem(ev + 25, 1)[0]]) as u64);
            emit(b")");
        }
    }
    e
}

// Makes the call a line names and traces it; an errno, or 0 once "ok" and the results are traced.
fn call(name: &[u8], a: &mut Args) -> i32 {
    let out = alloc(64);
    unsafe {
        let e = match name {
            b"args_get" => return emit_strings(args_get, args_sizes_get),
            b"environ_get" => return emit_strings(environ_get, environ_sizes_get),
            b"args_sizes_get" | b"environ_sizes_get" => {
                let e = if name == b"args_sizes_get" { args_sizes_get(out, out + 4) } else { environ_sizes_get(out, out + 4) };
                if e == 0 {
                    emit(b" ok ");
                    emit_u64(get_u32(out) as u64);
                    emit(b" ");
                    emit_u64(get_u32(out + 4) as u64);
                }
                return e;
            }
            b"clock_res_get" | b"clock_time_get" | b"fd_tell" => {
                let e = match name {
                    b"clock_res_get" => clock_res_get(a.i32(), out),
                    b"clock_time_get" => clock_time_get(a.i32(), a.int(), out),
                    _ => fd_tell(a.i32(), out),
                };
                if e == 0 {
                    emit(b" ok ");
                    emit_u64(get_u64(out));
                }
                return e;
            }
            b"fd_seek" => {
                let e = fd_seek(a.i32(), a.int(), a.i32(), out);
                if e == 0 {
                    emit(b" ok ");
                    emit_u64(get_u64(out));
                }
                return e;
            }
            b"fd_read" | b"fd_pread" | b"sock_recv" => {
                let fd = a.i32();
                let e;
                let (iovs, count);
                if name == b"fd_pread" {
                    let offset = a.int();
                    (iovs, count) = a.buffers();
                    e = fd_pread(fd, iovs, count, offset, out);
                } else if name == b"sock_recv" {
                    let flags = a.i32();
                    (iovs, count) = a.buffers();
                    e = sock_recv(fd, iovs, count, flags, out, out + 4);
                } else {
                    (iovs, count) = a.buffers();
                    e = fd_read(fd, iovs, count, out);
                }
                if e == 0 {
                    emit(b" ok");
                    emit_gathered(iovs, count, get_u32(out));
                }
                return e;
            }
            b"fd_write" | b"fd_pwrite" | b"sock_send" => {
                let fd = a.i32();
                let e = match name {
                    b"fd_pwrite" => {
                        let offset = a.int();
                        let (iovs, count) = a.iovecs();
                        fd_pwrite(fd, iovs, count, offset, out)
                    }
                    b"sock_send" => {
                        let flags = a.i32();
                        let (iovs, count) = a.iovecs();
                        sock_send(fd, iovs, count, flags, out)
                    }
                    _ => {
                        let (iovs, count) = a.iovecs();
                        fd_write(fd, iovs, count, out)
                    }
                };
                if e == 0 {
                    emit(b" ok ");
                    emit_u64(get_u32(out) as u64);
                }
                return e;
            }
            b"fd_fdstat_get" => {
                let e = fd_fdstat_get(a.i32(), out);
                if e == 0 {
                    emit(b" ok filetype=");
                    emit_u64(mem(out, 1)[0] as u64);
                    emit(b" flags=");
                    emit_u64(u16::from_le_bytes([mem(out + 2, 1)[0], mem(out + 3, 1)[0]]) as u64);
                    emit(b" base=");
                    emit_hex(get_u64(out + 8));
                    emit(b" inheriting=");
                    emit_hex(get_u64(out + 16));
                }
                return e;
            }
            b"fd_filestat_get" => {
                let e = fd_filestat_get(a.i32(), out);
                if e == 0 {
                    emit_filestat(out);
                }
                return e;
            }
            b"path_filestat_get" => {
                let fd = a.i32();
                let flags = a.i32();
                let (p, n) = a.string();
                let e = path_filestat_get(fd, flags, p, n, out);
                if e == 0 {
                    emit_filestat(out);
                }
                return e;
            }
            b"fd_prestat_get" => {
                let e = fd_prestat_get(a.i32(), out);
                if e == 0 {
                    emit(b" ok tag=");
                    emit_u64(mem(out, 1)[0] as u64);
                    emit(b" len=");
                    emit_u64(get_u32(out + 4) as u64);
                }
                return e;
            }
            b"fd_prestat_dir_name" | b"path_readlink" => {
                let e;
                let buf;
                let len;
                if name == b"path_readlink" {
                    let fd = a.i32();
                    let (p, n) = a.string();
                    len = a.i32();
                    buf = alloc(len as usize);
                    e = path_readlink(fd, p, n, buf, len, out);
                } else {
                    let fd = a.i32();
                    len = a.i32();
                    buf = alloc(len as usize);
                    e = fd_prestat_dir_name(fd, buf, len);
                    put_u32(out, len as u32);
                }
                if e == 0 {
                    emit(b" ok ");
                    emit_quoted(mem(buf, get_u32(out) as usize));
                }
                return e;
            }
            b"fd_readdir" => {
                let fd = a.i32();
                let len = a.i32();
                let cookie = a.int();
                let buf = alloc(len as usize);
                let e = fd_readdir(fd, buf, len, cookie, out);
                if e == 0 {
                    emit(b" ok used=");
                    emit_u64(get_u32(out) as u64);
                    emit_dirents(buf, get_u32(out));
                }
                return e;
            }
            b"path_open" | b"sock_accept" => {
                let e = if name == b"sock_accept" {
                    sock_accept(a.i32(), a.i32(), out)
                } else {
                    let fd = a.i32();
                    let dirflags = a.i32();
                    let (p, n) = a.string();
                    path_open(fd, dirflags, p, n, a.i32(), a.int(), a.int(), a.i32(), out)
                };
                if e == 0 {
                    emit(b" ok ");
                    emit_u64(get_u32(out) as u64);
                }
                return e;
            }
            b"random_get" => {
                let len = a.i32();
                let buf = alloc(len as usize);
                let e = random_get(buf, len);
                if e == 0 {
                    emit(b" ok ");
                    emit_quoted(mem(buf, len as usize));
                }
                return e;
            }
            b"poll_oneoff" => return poll(a),
            b"proc_exit" => {
                let code = a.i32();
                emit(b"\n");
                flush();
                proc_exit(code);
                0
            }
            b"fd_advise" => fd_advise(a.i32(), a.int(), a.int(), a.i32()),
            b"fd_allocate" => fd_allocate(a.i32(), a.int(), a.int()),
            b"fd_close" => fd_close(a.i32()),
            b"fd_datasync" => fd_datasync(a.i32()),
            b"fd_sync" => fd_sync(a.i32()),
            b"fd_fdstat_set_flags" => fd_fdstat_set_flags(a.i32(), a.i32()),
            b"fd_fdstat_set_rights" => fd_fdstat_set_rights(a.i32(), a.int(), a.int()),
            b"fd_filestat_set_size" => fd_filestat_set_size(a.i32(), a.int()),
            b"fd_filestat_set_times" => fd_filestat_set_times(a.i32(), a.int(), a.int(), a.i32()),
            b"fd_renumber" => fd_renumber(a.i32(), a.i32()),
            b"path_create_directory" | b"path_remove_directory" | b"path_unlink_file" => {
                let fd = a.i32();
                let (p, n) = a.string();
                match name {
                    b"path_create_directory" => path_create_directory(fd, p, n),
                    b"path_remove_directory" => path_remove_directory(fd, p, n),
                    _ => path_unlink_file(fd, p, n),
                }
            }
            b"path_filestat_set_times" => {
                let fd = a.i32();
                let flags = a.i32();
                let (p, n) = a.string();
                path_filestat_set_times(fd, flags, p, n, a.int(), a.int(), a.i32())
            }
            b"path_link" => {
                let oldfd = a.i32();
                let flags = a.i32();
                let (o, on) = a.string();
                let newfd = a.i32();
                let (n, nn) = a.string();
                path_link(oldfd, flags, o, on, newfd, n, nn)
            }
            b"path_rename" => {
                let fd = a.i32();
                let (o, on) = a.string();
                let newfd = a.i32();
                let (n, nn) = a.string();
                path_rename(fd, o, on, newfd, n, nn)
            }
            b"path_symlink" => {
                let (o, on) = a.string();
                let fd = a.i32();
                let (n, nn) = a.string();
                path_symlink(o, on, fd, n, nn)
            }
            b"proc_raise" => proc_raise(a.i32()),
            b"sched_yield" => sched_yield(),
            b"sock_shutdown" => sock_shutdown(a.i32(), a.i32()),
            _ => {
                emit(b" ?");
                return 0;
            }
        };
        if e == 0 {
            emit(b" ok");
        }
        e
    }
}

#[no_mangle]
pub extern "C" fn _start() {
    let input = unsafe { &mut *core::ptr::addr_of_mut!(INPUT) };
    let mut len = 0;
    loop {
        unsafe { ARENA_LEN = 0 };
        let iov = alloc(8);
        put_u32(iov, input[len..].as_ptr() as u32);
        put_u32(iov + 4, (input.len() - len) as u32);
        let n = alloc(4);
        if unsafe { fd_read(0, iov, 1, n) } != 0 || get_u32(n) == 0 || len == input.len() {
            break;
        }
        len += get_u32(n) as usize;
    }

    for line in input[..len].split(|&b| b == b'\n') {
        if line.is_empty() || line[0] == b'#' {
            continue;
        }
        unsafe { ARENA_LEN = 0 };
        emit(line);
        emit(b" ->");
        let mut a = Args { rest: line };
        let name = a.token().unwrap_or(b"");
        let e = call(name, &mut a);
        if e != 0 {
            emit(b" ");
            emit(ERRNOS.get(e as usize).copied().unwrap_or("?").as_bytes());
        }
        emit(b"\n");
        flush();
    }
}
