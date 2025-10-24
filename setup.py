#!/usr/bin/env python3
import os, sys, subprocess, time, shutil, pathlib, urllib.request

BASE = pathlib.Path(__file__).resolve().parent
ROOT = BASE if BASE.name != "current" else BASE.parent
CUR = ROOT / "current"
MODELS = ROOT / "models"
DATA = CUR / "data"
VENV = CUR / "venv"

TS = time.strftime("%Y%m%d_%H%M%S")
MODEL_TAG = "llama3.2:1b-instruct-fp16"
RELEASE = MODELS / "releases" / f"{MODEL_TAG.replace(':','-')}-{TS}"
SYM = MODELS / "current" / "llama1b"

def log(msg):
    print(f"[setup] {msg}", flush=True)

def run(cmd, env=None, check=True, capture=False):
    log(f"→ Running: {' '.join(map(str, cmd))}")
    r = subprocess.run(
        cmd, env=env, check=check, text=True,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.PIPE if capture else None
    )
    if capture and r.stdout.strip():
        log(f"  Output: {r.stdout.strip()}")
    return r.stdout.strip() if capture else ""

def ensure_dirs():
    log("Ensuring directory structure...")
    for d in [CUR, DATA, MODELS, MODELS/"current", MODELS/"releases", RELEASE]:
        d.mkdir(parents=True, exist_ok=True)
    log(f"Created base directories under {ROOT}")

def ensure_corpus():
    p = DATA / "corpus.jsonl"
    if p.exists():
        log("Corpus already exists — skipping")
        return
    log("Creating initial data/corpus.jsonl ...")
    p.write_text(
        "\n".join([
            '{"id":"doc1","title":"Underwriting Engine Overview","text":"The TRS Underwriting Engine evaluates business credit risk using a rule-based system, scorecards, and historical deal performance."}',
            '{"id":"doc2","title":"Deal Status Change Workflow","text":"Deal status transitions are triggered by rule evaluations and underwriting outcomes. The new system replaces MEF with dependency injection for deterministic rule execution."}',
            '{"id":"doc3","title":"Auto Decline Rules","text":"The auto-decline module checks for OFAC hits, mismatched tax IDs, and insufficient credit consent before declining a deal automatically."}'
        ]) + "\n",
        encoding="utf-8"
    )
    log("Corpus created successfully")

def venv_python() -> pathlib.Path:
    if sys.platform.startswith("win"):
        return VENV / "Scripts" / "python.exe"
    return VENV / "bin" / "python"

def ensure_venv():
    log("Checking Python virtual environment...")
    py = venv_python()
    if not py.exists():
        if VENV.exists() and not any(VENV.iterdir()):
            log("Removing empty venv directory...")
            shutil.rmtree(VENV)
        log("Creating new virtual environment...")
        run([sys.executable, "-m", "venv", str(VENV)])
    py = venv_python()
    log("Upgrading pip/setuptools/wheel...")
    run([str(py), "-m", "pip", "install", "-q", "--upgrade", "pip", "setuptools", "wheel"])
    log("Installing dependencies: faiss-cpu, numpy, sentence-transformers, requests ...")
    run([str(py), "-m", "pip", "install", "-q", "faiss-cpu", "numpy", "sentence-transformers", "requests"])
    log("Virtual environment ready")

def api_ok():
    try:
        with urllib.request.urlopen("http://127.0.0.1:11434/api/tags", timeout=1):
            return True
    except Exception:
        return False

def ensure_ollama():
    log("Checking Ollama installation...")
    if not shutil.which("ollama"):
        log("ERROR: Ollama not found on PATH")
        sys.exit(1)
    log("Ollama found")

def ensure_ollama_running(env):
    log("Ensuring Ollama server is running...")
    if api_ok():
        log("Ollama API already responding")
        return
    try:
        subprocess.Popen(["ollama", "serve"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except Exception:
        pass
    for i in range(30):
        if api_ok():
            log("Ollama server is up")
            return
        time.sleep(1)
    log("WARNING: Ollama API did not respond after 30 seconds")

def ensure_model(env):
    log(f"Ensuring model {MODEL_TAG} is available...")
    out = run(["ollama", "list"], env=env, capture=True)
    if MODEL_TAG not in out:
        log(f"Pulling model: {MODEL_TAG}")
        run(["ollama", "pull", MODEL_TAG], env=env)
    else:
        log(f"Model {MODEL_TAG} already present")

def snapshot(env):
    log("Saving model snapshot...")
    try:
        (RELEASE/"manifest.txt").write_text(run(["ollama","show",MODEL_TAG], env=env, capture=True))
    except Exception as e:
        log(f"Failed to save manifest: {e}")
    try:
        (RELEASE/"ollama-list.txt").write_text(run(["ollama","list"], env=env, capture=True))
    except Exception as e:
        log(f"Failed to save model list: {e}")
    SYM.parent.mkdir(parents=True, exist_ok=True)
    if SYM.exists() or SYM.is_symlink():
        SYM.unlink()
    SYM.symlink_to(RELEASE)
    log(f"Symlink updated: {SYM} → {RELEASE}")

def main():
    log("Starting setup process...")
    ensure_dirs()
    ensure_corpus()
    ensure_venv()
    ensure_ollama()
    env = os.environ.copy()
    env["OLLAMA_MODELS"] = str(MODELS)
    ensure_ollama_running(env)
    ensure_model(env)
    snapshot(env)
    log("✅ Setup completed successfully")
    print(f"\nSummary:\n"
          f"  root:    {ROOT}\n"
          f"  models:  {MODELS}\n"
          f"  current: {CUR}\n"
          f"  release: {RELEASE}\n")

if __name__ == "__main__":
    main()
