"""Build sliding story chunks from DEV transcripts -> ~/eval/r8e-b/chunks.json"""
import json, sys
from pathlib import Path
SPAN=float(sys.argv[1]) if len(sys.argv)>1 else 30; STRIDE=float(sys.argv[2]) if len(sys.argv)>2 else 12
DEV=["stvr-spravy-20261123","joj-krimi-20261123"]
out={}
for vid in DEV:
    T=json.load(open(Path.home()/f"eval/r8d/tr-small/{vid}.json"))
    T=[s for s in T if s["text"].strip()]
    chunks=[]; t=0.0; end=max(s["end"] for s in T)
    while t<end:
        segs=[s for s in T if s["start"]>=t and s["start"]<t+SPAN]
        if segs:
            chunks.append({"start":segs[0]["start"],"end":max(s["end"] for s in segs),"text":" ".join(s["text"].strip() for s in segs)})
        t+=STRIDE
    # dedupe identical
    seen=set(); cc=[]
    for c in chunks:
        k=(round(c["start"],1),round(c["end"],1))
        if k not in seen: seen.add(k); cc.append(c)
    out[vid]=cc
json.dump(out,open(Path.home()/f"eval/r8e-b/chunks_{int(SPAN)}_{int(STRIDE)}.json","w"),ensure_ascii=False)
print({k:len(v) for k,v in out.items()}, "avg words", sum(len(c["text"].split()) for v in out.values() for c in v)/sum(len(v) for v in out.values()))
