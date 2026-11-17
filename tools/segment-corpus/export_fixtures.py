"""Export the #598 segment-analysis research corpus as small test fixtures.

The research folder (nomercy-segment-research, runs/corpus) holds the recorded
per-episode inputs the prototype analysed: chromaprint prints, silences,
blackdetect runs, OCR word counts, edge probes and thumbnails. This script copies
what the C# pipeline's pure stages read, in the same layout, and shrinks it:

- audio.json keeps duration and per-part offset, fingerprint file and silences
  (the media paths are dropped);
- video.json keeps the black runs and the windows they were measured in;
- ocr.json keeps the start and the frames that held any word;
- thumbs.npz keeps only the thumbnails the prototype compared (plus 1 s around
  them), found by running the prototype with a recording frame lookup;
- fp_*.raw (the big part) is copied only for the seasons named in --fingerprints.
  The other seasons replay the prototype's recorded matches.json instead.

The prototype is re-run on the exported data and must give the same
analysis.json, or the export fails.

usage: python export_fixtures.py <research root> <out dir> [--seasons a,b] [--fingerprints a,b]
"""
import argparse
import contextlib
import io
import json
import shutil
import sys
import tempfile
from pathlib import Path

import numpy as np


def load_prototype(root: Path):
    sys.path.insert(0, str(root / "tools"))
    import analyze  # noqa: E402  (the research prototype)

    return analyze


def recording_visual_support(A, used):
    """visual_support from analyze.py, recording every thumbnail it reads."""

    def visual_support(ep, start, end, hits, by):
        if ep["thumbs"] is None:
            return None
        t_own, px_own = ep["thumbs"]
        res = {}
        for h in hits:
            lo, hi = max(start, h["start"]), min(end, h["end"])
            p = by[h["partner"]]
            if hi - lo < 4 or p["thumbs"] is None:
                continue
            t_p, px_p = p["thumbs"]
            off = h["p_start"] - h["start"]
            sel = np.flatnonzero((t_own >= lo) & (t_own <= hi))
            chk = res.setdefault(h["partner"], [0, 0])
            for k in sel:
                used.setdefault(ep["name"], set()).add(float(t_own[k]))
                want = t_own[k] + off
                j = int(np.searchsorted(t_p, want))
                cand = [x for x in (j - 1, j) if 0 <= x < len(t_p)]
                if not cand:
                    continue
                j = min(cand, key=lambda x: abs(t_p[x] - want))
                if abs(t_p[j] - want) > 0.3:
                    continue
                used.setdefault(p["name"], set()).add(float(t_p[j]))
                chk[1] += 1
                chk[0] += A._frame_match(px_own[k], px_p[j])
        checked = [v for v in res.values() if v[1] >= 6]
        if not checked:
            return None
        return sum(1 for m, n in checked if m / n >= A.VIS_FRAC), len(checked)

    return visual_support


def write_json(path: Path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, separators=(",", ":")), encoding="utf-8")


def export_season(A, src: Path, out: Path, with_fp: bool):
    used = {}
    original = A.visual_support
    A.visual_support = recording_visual_support(A, used)
    try:
        with tempfile.TemporaryDirectory() as td:
            run = Path(td)
            shutil.copytree(src, run / src.name, ignore=shutil.ignore_patterns("*.txt", "*_w3.json", "_*"))
            with contextlib.redirect_stdout(io.StringIO()):
                A.analyze(run, src.name)
    finally:
        A.visual_support = original

    out.mkdir(parents=True, exist_ok=True)
    for name in ("matches.json", "analysis.json"):
        write_json(out / name, json.loads((src / name).read_text(encoding="utf-8")))

    for ep in sorted(p for p in src.iterdir() if (p / "audio.json").exists()):
        dst = out / ep.name
        dst.mkdir(parents=True, exist_ok=True)
        audio = json.loads((ep / "audio.json").read_text(encoding="utf-8"))
        write_json(dst / "audio.json", {
            "episode": audio["episode"],
            "duration": audio["duration"],
            "parts": [{"offset": p["offset"], "fp": p["fp"], "silences": p["silences"]} for p in audio["parts"]],
        })
        if with_fp:
            for p in audio["parts"]:
                shutil.copyfile(ep / p["fp"], dst / p["fp"])
        for name in ("chapters.json", "edges.json"):
            if (ep / name).exists():
                write_json(dst / name, json.loads((ep / name).read_text(encoding="utf-8")))
        if (ep / "video.json").exists():
            video = json.loads((ep / "video.json").read_text(encoding="utf-8"))
            write_json(dst / "video.json", {"windows": video["windows"], "black": video["black"]})
        if (ep / "ocr.json").exists():
            ocr = json.loads((ep / "ocr.json").read_text(encoding="utf-8"))
            write_json(dst / "ocr.json", {"start": ocr["start"],
                                          "per_second": [s for s in ocr["per_second"] if s[1] > 0]})
        if (ep / "thumbs.npz").exists():
            z = np.load(ep / "thumbs.npz")
            t, px = z["t"], z["px"]
            want = np.array(sorted(used.get(ep.name, ())), dtype=np.float64)
            keep = np.zeros(len(t), dtype=bool)
            for w in want:
                keep |= np.abs(t - w) <= 1.0
            if keep.any():
                np.savez_compressed(dst / "thumbs.npz", t=t[keep], px=px[keep])
    return out


def verify(A, out: Path):
    """The prototype on the exported data must reproduce the recorded analysis."""
    with tempfile.TemporaryDirectory() as td:
        run = Path(td)
        shutil.copytree(out, run / out.name)
        for audio in (run / out.name).glob("*/audio.json"):
            for part in json.loads(audio.read_text(encoding="utf-8"))["parts"]:
                fp = audio.parent / part["fp"]
                if not fp.exists():
                    fp.write_bytes(b"")  # matches.json replaces the print
        with contextlib.redirect_stdout(io.StringIO()):
            A.analyze(run, out.name)
        got = json.loads((run / out.name / "analysis.json").read_text(encoding="utf-8"))
    want = json.loads((out / "analysis.json").read_text(encoding="utf-8"))
    strip = lambda r: [{k: v for k, v in e.items() if k != "trailingsilence"} for e in r["episodes"]]
    if json.dumps(strip(got), sort_keys=True) != json.dumps(strip(want), sort_keys=True):
        raise SystemExit(f"{out.name}: exported data does not reproduce analysis.json")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root", help="nomercy-segment-research folder")
    ap.add_argument("out")
    ap.add_argument("--seasons", default="")
    ap.add_argument("--fingerprints", default="")
    a = ap.parse_args()
    root, out = Path(a.root), Path(a.out)
    A = load_prototype(root)
    corpus = root / "runs" / "corpus"
    seasons = [s for s in a.seasons.split(",") if s] or sorted(
        p.name for p in corpus.iterdir() if (p / "analysis.json").exists())
    with_fp = set(s for s in a.fingerprints.split(",") if s)
    for s in seasons:
        dst = export_season(A, corpus / s, out / s, s in with_fp)
        verify(A, dst)
        size = sum(f.stat().st_size for f in dst.rglob("*") if f.is_file())
        print(f"{s}: {size / 1024:.0f} KiB")


if __name__ == "__main__":
    main()
