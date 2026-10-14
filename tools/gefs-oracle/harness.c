#include <u.h>
#include <libc.h>
#include <fcall.h>
#include <avl.h>

#include "dat.h"
#include "fns.h"

/*
 * Drives 9front gefs's tree.c, built with Fog's 48-byte block pointers, through a seeded
 * sequence of upserts, printing a digest of the tree's exact shape after each one.
 */

char Efs[] = "internal error";
char Eheight[] = "tree exceeds max height";
char Enomem[] = "out of memory";

Gefs *fs;
static Errctx ctx;
static Errctx *ctxp = &ctx;
Errctx **errctx = &ctxp;

enum { Maxblk = 1<<20 };
static Blk *store[Maxblk];
static Blk *dead[Maxblk];
static int nlimbo;
static vlong nextaddr = Blksz;

void _trace(char *m, Bptr bp, vlong a, vlong b) { USED(m); USED(bp); USED(a); USED(b); }
jmp_buf* _waserror(void) { return &ctx.errlab[ctx.nerrlab++]; }
void nexterror(void) { fprint(2, "error: %s\n", ctx.err); exits("error"); abort(); }

void
error(char *fmt, ...)
{
	va_list ap;

	va_start(ap, fmt);
	vsnprint(ctx.err, sizeof ctx.err, fmt, ap);
	va_end(ap);
	nexterror();
}

void
broke(char *fmt, ...)
{
	va_list ap;

	va_start(ap, fmt);
	vsnprint(ctx.err, sizeof ctx.err, fmt, ap);
	va_end(ap);
	nexterror();
}

void _babort(vlong a, char *c) { fprint(2, "assert %lld %s\n", a, c); abort(); }

char*
packbp(char *p, int sz, Bptr *bp)
{
	assert(sz >= Ptrsz);
	memset(p, 0, Ptrsz);
	PACK64(p, bp->addr);
	PACK64(p+8, bp->hash);
	PACK64(p+Ptrsz-8, bp->gen);
	return p + Ptrsz;
}

Bptr
unpackbp(char *p, int sz)
{
	Bptr bp;

	assert(sz >= Ptrsz);
	bp.addr = UNPACK64(p);
	bp.hash = UNPACK64(p+8);
	bp.gen = UNPACK64(p+Ptrsz-8);
	return bp;
}

Tree* unpacktree(Tree *t, char *p, int sz) { USED(t); USED(p); USED(sz); abort(); return nil; }
char* packtree(char *p, int sz, Tree *t) { USED(t); USED(p); USED(sz); abort(); return nil; }
void kv2dir(Kvp *kv, Xdir *d) { USED(kv); USED(d); abort(); }
char* packdval(char *p, int sz, Xdir *d) { USED(d); USED(p); USED(sz); abort(); return nil; }

static Blk*
initblk(Blk *b, vlong addr, int ty)
{
	memset(b, 0, sizeof *b);
	b->type = ty;
	b->bp.addr = addr;
	b->bp.hash = -1;
	b->bp.gen = 1;
	b->data = b->buf + (ty == Tpivot ? Pivhdsz : Leafhdsz);
	return b;
}

Blk*
newblk(Tree *t, int ty)
{
	Blk *b;

	USED(t);
	b = emalloc(sizeof(Blk), 1);
	initblk(b, nextaddr, ty);
	nextaddr += Blksz;
	return b;
}

Blk*
dupblk(Tree *t, Blk *b)
{
	Blk *r;

	r = newblk(t, b->type);
	r->nval = b->nval;
	r->valsz = b->valsz;
	r->nbuf = b->nbuf;
	r->bufsz = b->bufsz;
	memcpy(r->buf, b->buf, sizeof(r->buf));
	return r;
}

void*
emalloc(usize n, int zero)
{
	void *p;

	p = malloc(n);
	if(p == nil)
		sysfatal("out of memory");
	if(zero)
		memset(p, 0, n);
	return p;
}

void
enqueue(Blk *b)
{
	b->bp.hash = b->bp.addr;
	store[b->bp.addr / Blksz] = b;
}

Blk*
getblk(Bptr bp, int flg)
{
	Blk *b;

	USED(flg);
	b = store[bp.addr / Blksz];
	if(b == nil)
		sysfatal("missing block %lld", bp.addr);
	return b;
}

Blk* holdblk(Blk *b) { return b; }
void dropblk(Blk *b) { USED(b); }

void
freeblk(Tree *t, Blk *b)
{
	USED(t);
	dead[nlimbo++] = b;
}

void
freebp(Tree *t, Bptr bp)
{
	USED(t);
	USED(bp);
}

ushort
blkfill(Blk *b)
{
	switch(b->type){
	case Tpivot:
		return 2*b->nbuf + b->bufsz +  2*b->nval + b->valsz;
	case Tleaf:
		return 2*b->nval + b->valsz;
	default:
		abort();
	}
	return 0;
}

static uvlong rng;

static uvlong
next64(void)
{
	uvlong z;

	z = (rng += 0x9e3779b97f4a7c15ULL);
	z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9ULL;
	z = (z ^ (z >> 27)) * 0x94d049bb133111ebULL;
	return z ^ (z >> 31);
}

static int
between(int lo, int hi)
{
	return lo + next64() % (uvlong)(hi - lo + 1);
}

static uvlong digest;

static void
feed(void *p, int n)
{
	uchar *b;
	int i;

	b = p;
	for(i = 0; i < n; i++){
		digest ^= b[i];
		digest *= 0x100000001b3ULL;
	}
}

static void
feed16(int v)
{
	uchar b[2];

	PACK16(b, v);
	feed(b, 2);
}

static void
walk(Bptr bp)
{
	Blk *b;
	Kvp kv;
	Msg m;
	int i, fill;
	uchar t;

	b = getblk(bp, 0);
	t = b->type == Tleaf ? 'L' : 'P';
	feed(&t, 1);
	feed16(b->nval);
	feed16(b->type == Tpivot ? b->nbuf : 0);
	for(i = 0; i < b->nval; i++){
		getval(b, i, &kv);
		feed16(kv.nk);
		feed(kv.k, kv.nk);
		if(b->type == Tleaf){
			feed16(kv.nv);
			feed(kv.v, kv.nv);
		}else{
			getptr(&kv, &fill);
			feed16(fill);
			walk(unpackbp(kv.v, kv.nv));
		}
	}
	for(i = 0; b->type == Tpivot && i < b->nbuf; i++){
		getmsg(b, i, &m);
		feed(&m.op, 1);
		feed16(m.nk);
		feed(m.k, m.nk);
		feed16(m.nv);
		feed(m.v, m.nv);
	}
}

void
main(int argc, char **argv)
{
	int count, nkeys, minkey, maxkey, maxval, most, grow, step, i, n, k, roll, j;
	char **keys, *present, *pending;
	int *klen;
	Msg *msg;
	Tree t;
	Blk *root;
	Kvp sentinel;
	uchar ht;

	if(argc != 9)
		sysfatal("usage: harness seed count keys minkey maxkey maxval most grow");
	rng = strtoull(argv[1], nil, 10);
	count = atoi(argv[2]);
	nkeys = atoi(argv[3]);
	minkey = atoi(argv[4]);
	maxkey = atoi(argv[5]);
	maxval = atoi(argv[6]);
	most = atoi(argv[7]);
	grow = atoi(argv[8]);

	fs = emalloc(sizeof(Gefs), 1);
	qlock(&fs->mutlk);
	keys = emalloc(nkeys*sizeof(char*), 1);
	klen = emalloc(nkeys*sizeof(int), 1);
	present = emalloc(nkeys, 1);
	pending = emalloc(nkeys, 1);
	for(i = 0; i < nkeys; i++){
		klen[i] = between(minkey, maxkey);
		keys[i] = emalloc(klen[i], 1);
		for(j = 0; j < klen[i]; j++)
			keys[i][j] = next64();
		keys[i][0] = 0x10;
	}
	msg = emalloc(most*sizeof(Msg), 1);

	memset(&t, 0, sizeof t);
	/* gefs never leaves a leaf empty, so the tree starts with a key before every other. */
	root = newblk(&t, Tleaf);
	sentinel.k = "\x10";
	sentinel.nk = 1;
	sentinel.v = "";
	sentinel.nv = 0;
	setval(root, &sentinel);
	enqueue(root);
	t.bp = root->bp;
	t.ht = 1;

	for(step = 1; step <= count; step++){
		n = between(1, most);
		memcpy(pending, present, nkeys);
		for(i = 0; i < n; i++){
			k = between(0, nkeys-1);
			roll = between(0, 99);
			msg[i].k = keys[k];
			msg[i].nk = klen[k];
			msg[i].v = nil;
			msg[i].nv = 0;
			if(pending[k])
				msg[i].op = roll < (step <= grow ? 30 : 90) ? Odelete : roll < (step <= grow ? 90 : 97) ? Oinsert : Oclobber;
			else
				msg[i].op = roll < (step <= grow ? 85 : 5) ? Oinsert : roll < (step <= grow ? 92 : 60) ? Oclobber : Oclearb;
			if(msg[i].op == Oinsert){
				msg[i].nv = between(0, maxval);
				msg[i].v = emalloc(msg[i].nv + 1, 0);
				for(j = 0; j < msg[i].nv; j++)
					msg[i].v[j] = next64();
			}
			pending[k] = msg[i].op == Oinsert;
		}
		btupsert(&t, msg, n);
		memcpy(present, pending, nkeys);
		for(i = 0; i < nlimbo; i++)
			store[dead[i]->bp.addr / Blksz] = nil;
		nlimbo = 0;

		digest = 0xcbf29ce484222325ULL;
		ht = t.ht;
		feed(&ht, 1);
		walk(t.bp);
		print("%d %d %016llux\n", step, t.ht, digest);
	}
	exits(nil);
}
