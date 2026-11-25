#!/usr/bin/env python3
"""
Summarise all .trx files under a folder (CI, CR-001 3.):
  - prints failed tests (name, message, first stack lines),
  - counts run / passed / failed / skipped, lists skipped tests with their reason,
  - fails (exit 1) on a skipped test that is neither in --allowed-skips nor has an --allow-skip-reason,
  - reports the outcome of each --report test by name,
  - appends a Markdown table to $GITHUB_STEP_SUMMARY when set.

Usage: trx_summary.py TestResults [--title T] [--allowed-skips FILE] [--allow-skip-reason TEXT]... [--report TEST]...
Allowlist file: one fully qualified test name per line; '#' starts a comment (every entry must say why).
"""
import argparse, glob, os, sys
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}


def load_allowlist(path):
    names = set()
    if path:
        with open(path, encoding='utf-8-sig') as f:
            for line in f:
                name = line.split('#', 1)[0].strip()
                if name:
                    names.add(name)
    return names


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('root', nargs='?', default='TestResults')
    ap.add_argument('--title', default='Tests')
    ap.add_argument('--allowed-skips')
    ap.add_argument('--allow-skip-reason', action='append', default=[])
    ap.add_argument('--report', action='append', default=[])
    a = ap.parse_args()

    allowed = load_allowlist(a.allowed_skips)
    results = {}  # name -> (outcome, message)
    failed = []
    for f in sorted(glob.glob(os.path.join(a.root, '**', '*.trx'), recursive=True)):
        for r in ET.parse(f).getroot().iterfind('.//t:UnitTestResult', NS):
            name = r.get('testName')
            outcome = r.get('outcome')
            msg = r.find('.//t:Message', NS)
            text = (msg.text or '').strip() if msg is not None else ''
            results[name] = (outcome, text)
            if outcome == 'Failed':
                failed.append(name)
                print(f"FAILED {name}")
                if text:
                    print('  ' + text.replace('\n', '\n  ')[:1500])
                st = r.find('.//t:StackTrace', NS)
                if st is not None and st.text:
                    for l in [l.strip() for l in st.text.splitlines() if 'BackupForge' in l][:3]:
                        print('  ' + l[:300])

    skipped = {n: m for n, (o, m) in results.items() if o == 'NotExecuted'}
    passed = sum(1 for o, _ in results.values() if o == 'Passed')
    bad_skips = sorted(n for n, m in skipped.items()
                       if n not in allowed and not any(m == reason for reason in a.allow_skip_reason))

    print(f"{a.title}: {len(results)} run, {passed} passed, {len(failed)} failed, {len(skipped)} skipped")
    for n in sorted(skipped):
        tag = 'NOT ALLOWED' if n in bad_skips else 'allowed'
        print(f"  skipped ({tag}): {n} — {skipped[n]}")
    for n in a.report:
        o, m = results.get(n, ('missing', ''))
        print(f"  report: {n}: {o}" + (f" — {m}" if m and o != 'Passed' else ''))
    if bad_skips:
        print(f"ERROR: {len(bad_skips)} skipped test(s) not in the allowlist ({a.allowed_skips or 'none'})")

    summary = os.environ.get('GITHUB_STEP_SUMMARY')
    if summary:
        with open(summary, 'a', encoding='utf-8') as s:
            s.write(f"### {a.title}\n\n| run | passed | failed | skipped | not allowed skips |\n|---|---|---|---|---|\n")
            s.write(f"| {len(results)} | {passed} | {len(failed)} | {len(skipped)} | {len(bad_skips)} |\n\n")
            for n in a.report:
                o, m = results.get(n, ('missing', ''))
                s.write(f"- `{n}`: **{o}**" + (f" — {m[:300]}" if m and o != 'Passed' else '') + "\n")
            for n in failed:
                s.write(f"- FAILED `{n}`\n")
            for n in sorted(skipped):
                s.write(f"- skipped{' (NOT ALLOWED)' if n in bad_skips else ''}: `{n}` — {skipped[n][:200]}\n")
            s.write("\n")
    return 1 if bad_skips else 0


if __name__ == '__main__':
    sys.exit(main())
