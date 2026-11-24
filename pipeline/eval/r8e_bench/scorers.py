"""Candidate scorers: list[str] -> list[dict] (score + subtype)."""
import json
from pathlib import Path
import numpy as np, torch
torch.set_num_threads(4)
EVAL=Path.home()/"advideo-r8e-b/pipeline/eval"

def _protos():
    from advideo_pipeline import languages
    L=languages.active()
    pos=[(s,sub) for sub,ex in L.semantic_sensitive.items() for s in ex]
    return pos, list(L.semantic_neutral)

def _cases():
    c=json.load(open(EVAL/"text_safety_cases.json"))["cases"]
    return [x["text"] for x in c],[1 if x["expect"]=="sensitive" else 0 for x in c]

class Emb:
    def __init__(self,name,prefix="",maxlen=256):
        from transformers import AutoModel, AutoTokenizer
        self.tok=AutoTokenizer.from_pretrained(name); self.m=AutoModel.from_pretrained(name).eval(); self.prefix=prefix; self.maxlen=maxlen
    def __call__(self,texts,bs=16):
        out=[]
        for i in range(0,len(texts),bs):
            b=self.tok([self.prefix+t for t in texts[i:i+bs]],padding=True,truncation=True,max_length=self.maxlen,return_tensors="pt")
            with torch.no_grad(): h=self.m(**b).last_hidden_state
            m=b["attention_mask"].unsqueeze(-1).float(); v=(h*m).sum(1)/m.sum(1).clamp(min=1e-9)
            out.append(torch.nn.functional.normalize(v,dim=1))
        return torch.cat(out).numpy()

def proto_scorer(emb,k=3,lr=False):
    pos,neg=_protos(); P=emb([p for p,_ in pos]); N=emb(neg); labs=[s for _,s in pos]
    clf=None
    if lr:
        from sklearn.linear_model import LogisticRegression
        tx,ty=_cases(); X=np.vstack([emb(tx),P,N]); y=np.array(ty+[1]*len(P)+[0]*len(N))
        clf=LogisticRegression(C=1.0,max_iter=2000,class_weight="balanced").fit(X,y)
    def f(texts):
        E=emb(texts); sp=E@P.T; sn=E@N.T
        tp=np.sort(sp,1)[:,-k:].mean(1); tn=np.sort(sn,1)[:,-k:].mean(1); best=sp.argmax(1)
        pr=clf.predict_proba(E)[:,1] if clf is not None else [None]*len(texts)
        return [{"score":float(a-b),"subtype":labs[j],"lr":(float(p) if p is not None else None)} for a,b,j,p in zip(tp,tn,best,pr)]
    return f

HYP={"violence":"This text reports violence, an attack, a shooting or a murder.",
 "death":"This text reports that people died or were killed.",
 "war":"This text reports war, military attacks, missiles or fighting.",
 "terror":"This text reports terrorism, a bomb or hostages.",
 "disaster":"This text reports a disaster, a fire, a flood, an explosion or an earthquake.",
 "accident":"This text reports a serious accident with injured or dead people."}
def nli_scorer(name):
    from transformers import AutoModelForSequenceClassification, AutoTokenizer
    tok=AutoTokenizer.from_pretrained(name); m=AutoModelForSequenceClassification.from_pretrained(name).eval()
    lab={v.lower():k for k,v in m.config.id2label.items()}; ent=lab["entailment"]; con=lab.get("contradiction")
    subs=list(HYP); hyps=[HYP[s] for s in subs]
    def f(texts):
        prem=[t for t in texts for _ in hyps]; hy=hyps*len(texts)
        b=tok(prem,hy,truncation="only_first",max_length=320,padding=True,return_tensors="pt")
        with torch.no_grad(): lo=m(**b).logits
        # entailment vs contradiction (2-way) probability
        p=torch.softmax(lo[:,[ent,con]],-1)[:,0].view(len(texts),len(hyps)).numpy()
        return [{"score":float(r.max()),"subtype":subs[int(r.argmax())],"all":[round(float(x),3) for x in r]} for r in p]
    return f

def make(c):
    if c=="minilm": return proto_scorer(Emb("sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"))
    if c=="e5s": return proto_scorer(Emb("intfloat/multilingual-e5-small","query: ",512),lr=True)
    if c=="e5b": return proto_scorer(Emb("intfloat/multilingual-e5-base","query: ",512),lr=True)
    if c=="minilm_lr": return proto_scorer(Emb("sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"),lr=True)
    if c=="nli": return nli_scorer("MoritzLaurer/mDeBERTa-v3-base-xnli-multilingual-nli-2mil7")
    raise ValueError(c)

def llm_scorer(name):
    from transformers import AutoModelForCausalLM, AutoTokenizer
    tok=AutoTokenizer.from_pretrained(name); m=AutoModelForCausalLM.from_pretrained(name,torch_dtype=torch.float32).eval()
    tok.padding_side="left"
    yes=[tok.encode(w,add_special_tokens=False)[0] for w in ("YES"," YES","Yes")]; no=[tok.encode(w,add_special_tokens=False)[0] for w in ("NO"," NO","No")]
    sysmsg=("You check TV transcripts for brand safety. Answer YES if the text reports violence, a crime with victims, death, war or "
            "military attacks, terrorism, a disaster, a fire or a serious accident with injured or dead people. Answer NO for "
            "everything else (politics, economy, health, routine police checks, thefts without victims, culture, sport, weather). "
            "Answer with one word: YES or NO.")
    def f(texts):
        prompts=[tok.apply_chat_template([{"role":"system","content":sysmsg},{"role":"user","content":"Transcript (may be Slovak, Czech, Hungarian, Polish, German or English):\n"+t[:1200]}],tokenize=False,add_generation_prompt=True) for t in texts]
        b=tok(prompts,return_tensors="pt",padding=True)
        with torch.no_grad(): lo=m(**b).logits[:,-1,:]
        lp=torch.log_softmax(lo,-1)
        y=torch.logsumexp(lp[:,yes],-1); n=torch.logsumexp(lp[:,no],-1)
        p=torch.sigmoid(y-n).numpy()
        return [{"score":float(x),"subtype":"llm"} for x in p]
    return f
_make=make
def make(c):
    if c=="qwen05": return llm_scorer("Qwen/Qwen2.5-0.5B-Instruct")
    return _make(c)
