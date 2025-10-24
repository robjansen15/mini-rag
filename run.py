#!/usr/bin/env python3
import os, sys, time, json, numpy as np, faiss, requests
from sentence_transformers import SentenceTransformer

BASE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(BASE, "data", "corpus.jsonl")
MODEL_TAG = "llama3.2:1b-instruct-fp16"
HOST = "http://127.0.0.1:11434"
NUM_PREDICT = int(os.getenv("NUM_PREDICT", "300"))

def log(msg):
    print(f"[run] {msg}", flush=True)

def ensure_venv_exec():
    venv = os.path.join(BASE, "venv", "bin", "python")
    if os.path.exists(venv) and os.path.realpath(sys.executable) != os.path.realpath(venv):
        log("Re-executing inside virtual environment...")
        os.execv(venv, [venv] + sys.argv)

def load_corpus(path):
    log(f"Loading corpus from: {path}")
    with open(path, "r", encoding="utf-8") as f:
        entries = [json.loads(l) for l in f]
    texts = [e["text"] for e in entries]
    log(f"Loaded {len(texts)} documents.")
    return texts

def build_index(texts):
    log("Encoding corpus with sentence-transformer (this may take a moment)...")
    start = time.time()
    enc = SentenceTransformer("all-MiniLM-L6-v2")
    emb = np.array(enc.encode(texts, show_progress_bar=True), dtype="float32")
    duration = time.time() - start
    log(f"Embeddings created in {duration:.2f}s. Shape: {emb.shape}")
    idx = faiss.IndexFlatL2(emb.shape[1])
    idx.add(emb)
    log(f"FAISS index ready ({idx.ntotal} vectors).")
    return enc, idx

def retrieve(q, enc, idx, texts, k=3):
    log(f"Retrieving top-{k} documents for query: {q!r}")
    qv = np.array(enc.encode([q]), dtype="float32")
    _, I = idx.search(qv, k)
    results = [texts[i] for i in I[0]]
    log(f"Found {len(results)} relevant chunks.")
    return results

def stream_generate(prompt, model=MODEL_TAG, host=HOST, num_predict=NUM_PREDICT):
    log(f"Starting generation from model: {model}")
    url = f"{host}/api/generate"
    payload = {"model": model, "prompt": prompt, "stream": True, "options": {"num_predict": num_predict}}
    s = requests.post(url, json=payload, stream=True)
    s.raise_for_status()
    start = time.time()
    generated = ""
    tokens = 0
    last = time.time()

    for line in s.iter_lines(decode_unicode=True):
        if not line:
            continue
        try:
            obj = json.loads(line)
        except Exception:
            continue
        if "response" in obj:
            chunk = obj["response"]
            generated += chunk
            tokens += max(1, len(chunk.split()))
        done = obj.get("done", False)
        now = time.time()
        if now - last >= 0.1 or done:
            pct = min(99, int(tokens / max(1, num_predict) * 100))
            rate = tokens / max(0.001, (now - start))
            rem = max(0, num_predict - tokens)
            eta = int(rem / max(0.001, rate))
            sys.stdout.write(f"\r[generate] {pct:3d}% | tokens={tokens} | {rate:.1f} t/s | ETA {eta:ds}")
            sys.stdout.flush()
            last = now
        if done:
            break

    sys.stdout.write(f"\r[generate] 100% | tokens={tokens} | done\n")
    sys.stdout.flush()
    log("Generation complete.")
    return generated.strip()

if __name__ == "__main__":
    log("Initializing RAG runtime...")
    ensure_venv_exec()
    if not os.path.exists(DATA):
        log(f"ERROR: corpus not found at {DATA}")
        sys.exit(1)

    texts = load_corpus(DATA)
    enc, idx = build_index(texts)
    log("RAG index ready.")
    log("You can now call retrieve(query, enc, idx, texts) or stream_generate(prompt).")
    print("\nExample:")
    print("  results = retrieve('explain underwriting workflow', enc, idx, texts)")
    print("  print(stream_generate('Summarize: ' + results[0]))")
