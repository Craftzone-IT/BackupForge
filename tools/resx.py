#!/usr/bin/env python3
"""Add or update a string in a Strings.resx / Strings.hu.resx pair.

usage: tools/resx.py <dir-with-Strings.resx> <key> <english> <hungarian>
       tools/resx.py <dir> --batch <tsv-file>   (key<TAB>en<TAB>hu per line)
       --base <Name> before the other arguments: <Name>.resx / <Name>.hu.resx (default Strings; the GUI uses UiStrings)
"""
import sys, os
import xml.etree.ElementTree as ET

HEADER = '''<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
</root>
'''

def load(path):
    if not os.path.exists(path):
        with open(path, 'w', encoding='utf-8') as f: f.write(HEADER)
    return ET.parse(path)

def put(tree, key, value):
    root = tree.getroot()
    for d in root.findall('data'):
        if d.get('name') == key:
            d.find('value').text = value
            return
    d = ET.SubElement(root, 'data', {'name': key, '{http://www.w3.org/XML/1998/namespace}space': 'preserve'})
    ET.SubElement(d, 'value').text = value

def save(tree, path):
    ET.indent(tree, '  ')
    tree.write(path, encoding='utf-8', xml_declaration=True)

def main():
    base = 'Strings'
    if len(sys.argv) > 2 and sys.argv[1] == '--base':
        base = sys.argv[2]
        del sys.argv[1:3]
    d = sys.argv[1]
    en_p, hu_p = os.path.join(d, base + '.resx'), os.path.join(d, base + '.hu.resx')
    en, hu = load(en_p), load(hu_p)
    if sys.argv[2] == '--batch':
        rows = [l.rstrip('\n').split('\t') for l in open(sys.argv[3], encoding='utf-8') if l.strip() and not l.startswith('#')]
    else:
        rows = [sys.argv[2:5]]
    for k, e, h in rows:
        put(en, k, e.replace('\\n', '\n')); put(hu, k, h.replace('\\n', '\n'))  # "\n" in a TSV = a line break
    save(en, en_p); save(hu, hu_p)

main()
