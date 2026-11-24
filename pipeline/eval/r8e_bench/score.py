"""Score chunks with a candidate -> ~/eval/r8e-b/scores_<cand>_<chunkset>.json (resumable, cached per text).
usage: score.py <cand> <chunkset e.g. 30_12> [budget_sec]"""
import json, sys, time, hashlib
from pathlib import Path
sys.path.insert(0, str(Path.home()/"advideo-r8e-b/pipeline/src"))
cand, cs = sys.argv[1], sys.argv[2]; budget=float(sys.argv[3]) if len(sys.argv)>3 else 150
D=Path.home()/"eval/r8e-b"; chunks=json.load(open(D/f"chunks_{cs}.json"))
out_p=D/f"scores_{cand}_{cs}.json"; out=json.load(open(out_p)) if out_p.exists() else {}
cache_p=D/f"cache_{cand}.json"; cache=json.load(open(cache_p)) if cache_p.exists() else {}
from scorers import make
t0=time.time(); sc=make(cand); load=time.time()-t0
todo=[c["text"] for v in chunks.values() for c in v if hashlib.md5(c["text"].encode()).hexdigest() not in cache]
print("load",round(load,1),"todo",len(todo),flush=True)
B=int(__import__("os").environ.get("B","8")); n=0; tt=time.time()
for i in range(0,len(todo),B):
    if time.time()-t0>budget: break
    batch=todo[i:i+B]
    for txt,s in zip(batch, sc(batch)):
        cache[hashlib.md5(txt.encode()).hexdigest()]=s
    n+=len(batch)
json.dump(cache,open(cache_p,"w"))
if n: print("ms/chunk",round((time.time()-tt)*1000/n,1))
left=sum(1 for v in chunks.values() for c in v if hashlib.md5(c["text"].encode()).hexdigest() not in cache)
if not left:
    out={vid:[{**{k:c[k] for k in ("start","end")}, "score":cache[hashlib.md5(c["text"].encode()).hexdigest()]} for c in v] for vid,v in chunks.items()}
    json.dump(out,open(out_p,"w")); print("done")
else: print("left",left)
