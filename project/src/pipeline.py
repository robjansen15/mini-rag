import argparse
txt = read_text(path) if embed_text else None
return Record(
path=os.path.abspath(path),
rel=os.path.relpath(path, root),
size=int(st.st_size),
mtime=float(st.st_mtime),
sha256=sha256_of_file(path),
lang=lang_of(path) if ext else "unknown",
depth=int(depth),
text=txt,
)

def write_jsonl(records: Iterable[Record], outfile: io.TextIOBase) -> int:
n = 0
for r in records:
if r is None:
continue
outfile.write(r.to_json())
outfile.write("\n")
n += 1
return n

def cmd_extract(args: argparse.Namespace) -> int:
root = os.path.abspath(args.path)
if not os.path.isdir(root):
print("not a directory", file=sys.stderr)
return 2
recs: List[Record] = []
for p, d in iter_files(root, args.depth):
r = to_record(root, p, d, embed_text=not args.no_text)
if r:
recs.append(r)
os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
with open(args.out, "w", encoding="utf-8") as f:
n = write_jsonl(recs, f)
print(n)
return 0

def shorten_path(path: str, keep: int = 3) -> str:
parts = [p for p in path.split(os.sep) if p]
if len(parts) <= keep:
return path
head = parts[:2]
tail = parts[-(keep - 2):]
return os.sep.join(head + ["..."] + tail)

def cmd_tree(args: argparse.Namespace) -> int:
root = os.path.abspath(args.path)
width = args.width
by_dir: Dict[str, List[str]] = {}
for p, d in iter_files(root, args.depth):
rel = os.path.relpath(p, root)
dirp = os.path.dirname(rel)
by_dir.setdefault(dirp, []).append(os.path.basename(p))
lines: List[str] = []
for dirp in sorted(by_dir.keys()):
lines.append(dirp or ".")
for name in by_dir[dirp]:
s = f" - {name}"
if len(s) > width:
s = s[:width-1]
lines.append(s)
sys.stdout.write("\n".join(lines) + "\n")
return 0

def build_parser() -> argparse.ArgumentParser:
p = argparse.ArgumentParser(prog="pipeline.py")
sub = p.add_subparsers(dest="cmd", required=True)
x = sub.add_parser("extract")
x.add_argument("path")
x.add_argument("--depth", type=int, default=8)
x.add_argument("--out", default="corpus.jsonl")
x.add_argument("--no-text", action="store_true")
x.set_defaults(func=cmd_extract)
t = sub.add_parser("tree")
t.add_argument("path")
t.add_argument("--depth", type=int, default=4)
t.add_argument("--width", type=int, default=120)
t.set_defaults(func=cmd_tree)
return p

def main(argv: Optional[List[str]] = None) -> int:
parser = build_parser()
args = parser.parse_args(argv)
return args.func(args)

if __name__ == "__main__":
raise SystemExit(main())