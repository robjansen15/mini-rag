#!/usr/bin/env python3
import os, sys, json, requests

MODEL_TAG="llama3.2:1b-instruct-fp16"
HOST="http://127.0.0.1:11434"

def ensure_venv_exec():
    base=os.path.dirname(os.path.abspath(__file__))
    venv=os.path.join(base,"venv","bin","python")
    if os.path.exists(venv) and os.path.realpath(sys.executable)!=os.path.realpath(venv):
        os.execv(venv,[venv]+sys.argv)
ensure_venv_exec()

msg=os.environ.get("MSG") or (sys.argv[1] if len(sys.argv)>1 else f"Hello, confirm you're running {MODEL_TAG}")

payload={"model":MODEL_TAG,"messages":[{"role":"user","content":msg}],"stream":False}
r=requests.post(f"{HOST}/api/chat",json=payload)
r.raise_for_status()
obj=r.json()
out=obj.get("message",{}).get("content","") or obj.get("response","")
print(out)
