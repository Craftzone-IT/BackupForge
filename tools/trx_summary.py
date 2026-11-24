#!/usr/bin/env python3
"""Print failed tests (name, message, first stack line) from all .trx files under a folder — short CI output."""
import glob, os, sys
import xml.etree.ElementTree as ET

ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
root = sys.argv[1] if len(sys.argv) > 1 else 'TestResults'
failed = 0
total = 0
for f in glob.glob(os.path.join(root, '**', '*.trx'), recursive=True):
    tree = ET.parse(f)
    for r in tree.getroot().iterfind('.//t:UnitTestResult', ns):
        total += 1
        if r.get('outcome') != 'Failed':
            continue
        failed += 1
        msg = r.find('.//t:Message', ns)
        st = r.find('.//t:StackTrace', ns)
        print(f"FAILED {r.get('testName')}")
        if msg is not None and msg.text:
            print('  ' + msg.text.strip().replace('\n', '\n  ')[:1500])
        if st is not None and st.text:
            lines = [l.strip() for l in st.text.splitlines() if 'BackupForge' in l][:3]
            for l in lines:
                print('  ' + l[:300])
print(f'{failed} failed of {total}')
