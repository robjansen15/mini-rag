#!/usr/bin/env python3
import os, sys, json, argparse, pathlib, hashlib

BASE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.join(BASE, "data")
OUT_PATH = os.path.join(OUT_DIR, "corpus.jsonl")

ALLOWED_EXTS = {
    ".py",".ipynb",".js",".mjs",".cjs",".ts",".tsx",".jsx",".vue",".svelte",".java",".kt",".kts",".scala",".go",".rs",
    ".c",".h",".cpp",".cc",".cxx",".hpp",".hh",".m",".mm",".cs",".fs",".fsx",".php",".rb",".swift",".lua",".pl",".pm",".r",
    ".dart",".groovy",".gradle",".sql",".proto",".graphql",".gql",
    ".json",".json5",".toml",".ini",".cfg",".conf",".yaml",".yml",".env",".properties",".xml",
    ".html",".htm",".css",".scss",".sass",".less",
    ".md",".markdown",".rst",".adoc",".txt",".csv",".tsv",".log",".org",
}
SPECIAL_BASENAMES = {
    "Dockerfile":"dockerfile",
    "Makefile":"make",
    "CMakeLists.txt":"cmake",
    "BUILD":"bazel",
    "WORKSPACE":"bazel",
    "Podfile":"cocoapods",
    "Gemfile":"ruby-gems",
    "requirements.txt":"python-reqs",
    "environment.yml":"conda-env",
    "Pipfile":"pipenv",
    "Pipfile.lock":"pipenv-lock",
    "package.json":"npm",
    "pnpm-lock.yaml":"pnpm-lock",
    "yarn.lock":"yarn-lock",
    "poetry.lock":"poetry-lock",
    "pyproject.toml":"pyproject",
    "Cargo.toml":"cargo",
    "Cargo.lock":"cargo-lock",
    "go.mod":"gomod",
    "go.sum":"gosum",
    "composer.json":"composer",
    "composer.lock":"composer-lock",
    "pom.xml":"maven",
    "build.gradle.kts":"gradle-kts",
    "build.gradle":"gradle",
    ".gitignore":"git",
    ".gitattributes":"git",
    ".editorconfig":"editor",
}
IGNORE_DIRS = {
    ".git",".hg",".svn",".bzr","node_modules","dist","build","out","target",
    ".idea",".vscode",".vs","__pycache__",".venv","venv",".mypy_cache",".pytest_cache",".gradle",".next",".nuxt",".parcel-cache"
}

MAX_BYTES = int(os.getenv("EXTRACT_MAX_BYTES", str(2*1024*1024)))
ENCODINGS = ["utf-8","utf-16","latin-1"]

def ensure_venv_exec():
    venv = os.path.join(BASE, "venv", "bin", "python")
    if os.path.exists(venv) and os.path.realpath(sys.executable) != os.path.realpath(venv):
        os.execv(venv, [venv] + sys.argv)

def is_allowed_file(p: pathlib.Path) -> bool:
    if p.name in SPECIAL_BASENAMES:
        return True
    return p.suffix.lower() in ALLOWED_EXTS

def read_text_file(path: str) -> str:
    size = os.path.getsize(path)
    if size > MAX_BYTES:
        with open(path, "rb") as f:
            data = f.read(MAX_BYTES)
        return data.decode("utf-8", errors="ignore")
    for enc in ENCODINGS:
        try:
            with open(path, "r", encoding=enc, errors="strict") as f:
                return f.read()
        except Exception:
            continue
    with open(path, "rb") as f:
        return f.read().decode("utf-8", errors="ignore")

def detect_lang(p: pathlib.Path) -> str:
    if p.name in SPECIAL_BASENAMES:
        return SPECIAL_BASENAMES[p.name]
    ext = p.suffix.lower().lstrip(".")
    return ext or "plain"

def tree_lines(root: str):
    lines = []
    root_abs = os.path.abspath(root)
    for cur, dirs, files in os.walk(root_abs):
        dirs[:] = [d for d in dirs if d not in IGNORE_DIRS]
        rel = os.path.relpath(cur, root_abs)
        depth = 0 if rel == "." else len(pathlib.Path(rel).parts)
        if depth > 0:
            lines.append(("    " * (depth - 1)) + "└── " + os.path.basename(cur))
        for fname in sorted(files):
            fpath = os.path.join(cur, fname)
            p = pathlib.Path(fpath)
            if not is_allowed_file(p):
                continue
            lines.append(("    " * depth) + "├── " + fname)
    return lines

def file_iter(root: str):
    root_abs = os.path.abspath(root)
    for cur, dirs, files in os.walk(root_abs):
        dirs[:] = [d for d in dirs if d not in IGNORE_DIRS]
        for fname in files:
            fpath = os.path.join(cur, fname)
            p = pathlib.Path(fpath)
            if not is_allowed_file(p):
                continue
            yield p

def sha256_text(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8", errors="ignore")).hexdigest()

def write_jsonl(manifest_line: dict, items: list):
    os.makedirs(OUT_DIR, exist_ok=True)
    with open(OUT_PATH, "w", encoding="utf-8") as w:
        w.write(json.dumps(manifest_line, ensure_ascii=False) + "\n")
        for obj in items:
            w.write(json.dumps(obj, ensure_ascii=False) + "\n")

def build_text_with_context(root: str, rel: str, abs_path: str, lang: str, content: str) -> str:
    header = (
        f"PATH: {rel}\n"
        f"ABS_PATH: {abs_path}\n"
        f"ROOT: {root}\n"
        f"LANG: {lang}\n"
        f"---\n"
    )
    return header + content

def main():
    ensure_venv_exec()
    ap = argparse.ArgumentParser(prog="extract.py")
    ap.add_argument("project", help="Path to project root")
    args = ap.parse_args()

    root = os.path.abspath(args.project)
    if not os.path.isdir(root):
        print(f"not a directory: {root}", file=sys.stderr)
        sys.exit(2)

    tlines = tree_lines(root)
    manifest = {
        "path": "__TREE__",
        "text": "\n".join([os.path.basename(root)] + tlines),
        "root": root,
        "type": "tree"
    }

    items = []
    count = 0
    for p in file_iter(root):
        rel = os.path.relpath(str(p), root)
        try:
            txt = read_text_file(str(p))
        except Exception:
            continue
        lang = detect_lang(p)
        abs_path = str(p)
        text_with_ctx = build_text_with_context(root, rel, abs_path, lang, txt)
        obj = {
            "path": rel,
            "abs_path": abs_path,
            "root": root,
            "lang": lang,
            "size": os.path.getsize(abs_path),
            "hash": sha256_text(txt),
            "text": text_with_ctx
        }
        items.append(obj)
        count += 1

    write_jsonl(manifest, items)
    print(f"wrote {OUT_PATH} with {count} files and tree manifest")

if __name__ == "__main__":
    main()
