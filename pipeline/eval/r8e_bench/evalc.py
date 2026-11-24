"""Evaluate chunk-score candidates + lexicon with the production window rule on DEV.
usage: evalc.py <cand> <chunkset> <field score|lr> [grid|high,medium]"""
import json, sys, itertools
from pathlib import Path
sys.path.insert(0, str(Path.home()/"advideo-r8e-b/pipeline/src"))
from advideo_pipeline.aggregate import aggregate_segments
from advideo_pipeline.models import Detection
from advideo_pipeline.textmatch import safety_detections
D=Path.home()/"eval/r8e-b"; LAB=Path.home()/"advideo-r8e-b/pipeline/eval/broadcast"
DEV=["stvr-spravy-20261123","joj-krimi-20261123"]
labels={v:json.load(open(LAB/f"{v}.labels.json")) for v in DEV}
lex={}
for v in DEV:
    T=json.load(open(Path.home()/f"eval/r8d/tr-small/{v}.json"))
    lex[v]=[(float(s["start"]),float(s["end"]),d) for s in T for d in safety_detections(s["text"],"audio",0.8)]
def load(cand,cs):
    sc=json.load(open(D/f"scores_{cand}_{cs}.json"))
    return {v:[{"start":c["start"],"end":c["end"],**c["score"]} for c in L] for v,L in sc.items()}

def metrics(scores, field, hi, med, context=1):
    S=[0,0];N=[0,0];sh={};fs=[]
    for v in DEV:
        L=labels[v]; dets=list(lex[v])
        if scores is not None:
            for c in scores[v]:
                s=c.get(field)
                if s is None: continue
                sev="high" if s>=hi else "medium" if s>=med else None
                if sev: dets.append((c["start"],c["end"],Detection(term="speech: ~"+c.get("subtype","x"),score=0.8,kind="safety_speech",category="violence",categories=["violence","sensitive"],severity=sev)))
        W=aggregate_segments(dets,L["durationSec"],text_safety_context=context)
        for w in W:
            mid=(w.start_sec+w.end_sec)/2
            st=next((q for q in L["stories"] if q["start"]<=mid<q["end"]),None)
            if not st or st["label"] not in "SN": continue
            f="sensitive" in w.categories; k=(v,st["start"])
            (S if st["label"]=="S" else N)[0]+=f; (S if st["label"]=="S" else N)[1]+=1
            sh[k]=(st["label"],sh.get(k,(st["label"],False))[1] or f)
    sr=sum(1 for l,f in sh.values() if l=="S" and f); sn=sum(1 for l,f in sh.values() if l=="S")
    fr=sum(1 for l,f in sh.values() if l=="N" and f); nn=sum(1 for l,f in sh.values() if l=="N")
    return dict(wr=round(S[0]/S[1],3),sr=f"{sr}/{sn}",fbr=round(N[0]/N[1],4),fs=f"{fr}/{nn}",_sr=sr,_fbr=N[0]/N[1])
if __name__=="__main__":
    cand,cs,field=sys.argv[1:4]
    if cand=="lexicon": print("lexicon",metrics(None,"score",9,9)); sys.exit()
    sc=load(cand,cs)
    vals=sorted(c[field] for v in sc.values() for c in v if c.get(field) is not None)
    qs=[vals[int(len(vals)*q)] for q in (0.5,0.6,0.7,0.75,0.8,0.85,0.9,0.93,0.95,0.97,0.98,0.99)]
    if len(sys.argv)>4 and sys.argv[4]!="grid":
        hi,med=map(float,sys.argv[4].split(",")); print(cand,cs,field,hi,med,metrics(sc,field,hi,med)); sys.exit()
    res=[]
    for hi in qs+[9]:
        for med in qs:
            if med>hi: continue
            m=metrics(sc,field,hi,med); res.append((hi,med,m))
    # best: story recall max then fbr min, constraint fbr
    print(cand,cs,field)
    for target in (0.01,0.015,0.02,0.03):
        ok=[r for r in res if r[2]["_fbr"]<=target]
        if ok:
            b=max(ok,key=lambda r:(r[2]["_sr"],r[2]["wr"]))
            print(f"  fbr<={target}: hi={b[0]:.3f} med={b[1]:.3f}",{k:v for k,v in b[2].items() if not k.startswith('_')})
