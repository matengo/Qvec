"""Aggregate a dotnet-trace speedscope export into self/inclusive time per frame.

Usage: python profile-summary.py trace.speedscope.json [--top 40] [--filter Qvec] [--callers Frame]

dotnet-trace writes one "evented" profile per thread. Each open/close pair is a stack push/pop
with a timestamp; we walk them to attribute self time to the innermost frame and inclusive
time to every frame on the stack (once per frame per sample, so recursion is not double
counted). Frames are aggregated across threads.
"""
import argparse
import json
import sys
from collections import defaultdict


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def summarise(doc):
    frames = [f["name"] for f in doc["shared"]["frames"]]
    self_t = defaultdict(float)
    incl_t = defaultdict(float)
    callers = defaultdict(lambda: defaultdict(float))  # callee -> caller -> self time of callee under caller
    total = 0.0
    # dotnet-trace appends pseudo leaves such as CPU_TIME / UNMANAGED_CODE_TIME / BLOCKED_TIME
    # under the real frame; self time belongs to the innermost real frame above them. Only
    # CPU_TIME samples count, so blocked threads do not dilute the percentages.
    pseudo = {"CPU_TIME", "UNMANAGED_CODE_TIME", "BLOCKED_TIME", "Threads", "(Non-Activities)"}

    def is_real(name):
        return name not in pseudo and not name.startswith(("Process", "Thread ("))

    for prof in doc["profiles"]:
        if prof["type"] != "evented":
            continue
        stack = []
        last_t = prof["startValue"]
        for ev in prof["events"]:
            t = ev["at"]
            dt = t - last_t
            if dt > 0 and stack and frames[stack[-1]] != "BLOCKED_TIME":
                total += dt
                real = [frames[fi] for fi in stack if is_real(frames[fi])]
                if real:
                    top = real[-1]
                    self_t[top] += dt
                    if len(real) > 1:
                        callers[top][real[-2]] += dt
                for name in set(real):
                    incl_t[name] += dt
            last_t = t
            if ev["type"] == "O":
                stack.append(ev["frame"])
            else:
                if stack:
                    stack.pop()
    return total, self_t, incl_t, callers


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--filter", default=None, help="only show frames containing this text")
    ap.add_argument("--callers", default=None, help="show callers of frames containing this text")
    args = ap.parse_args()

    doc = load(args.path)
    total, self_t, incl_t, callers = summarise(doc)
    unit = doc["profiles"][0].get("unit", "ms")
    print(f"total sampled time: {total:,.0f} {unit} over {len(doc['profiles'])} threads\n")

    def show(title, table):
        rows = sorted(table.items(), key=lambda kv: -kv[1])
        if args.filter:
            rows = [r for r in rows if args.filter in r[0]]
        print(f"== {title} (top {args.top}) ==")
        print(f"{'%':>6} {'time':>12}  frame")
        for name, t in rows[: args.top]:
            print(f"{100 * t / total:6.2f} {t:12,.0f}  {name}")
        print()

    show("self time", self_t)
    show("inclusive time", incl_t)

    if args.callers:
        for callee, by_caller in callers.items():
            if args.callers in callee:
                print(f"== callers of {callee} ==")
                for caller, t in sorted(by_caller.items(), key=lambda kv: -kv[1])[:15]:
                    print(f"{100 * t / total:6.2f} {t:12,.0f}  {caller}")
                print()


if __name__ == "__main__":
    main()
