"""Offline candidate checks only: does not install files or prove gameplay."""
import copy
import json
import sys
from pathlib import Path
import xml.etree.ElementTree as ET


def values(node):
    return {c.attrib['name']: c.attrib['value'] for c in node if 'value' in c.attrib}


def check(root, palette):
    info = root.find("imgdir[@name='info']")
    assert info.find("float[@name='mobRate']") is not None
    assert not any(c.attrib['name'] in ('link', 'onUserEnter', 'onFirstUserEnter') for c in info)
    footholds = {int(f.attrib['name']): {k: int(v) for k, v in values(f).items()}
                 for f in root.findall("imgdir[@name='foothold']/imgdir/imgdir/imgdir")}
    assert len(footholds) == 30
    for fid, f in footholds.items():
        assert f['y1'] == f['y2'] == 0 and f['x2'] > f['x1']
        for direction, opposite, xend, xstart in [('next', 'prev', 'x2', 'x1'), ('prev', 'next', 'x1', 'x2')]:
            if f[direction]:
                adjacent = footholds[f[direction]]
                assert adjacent[opposite] == fid and adjacent[xstart] == f[xend]
    seen = set()
    fid = 1
    while fid:
        assert fid not in seen
        seen.add(fid)
        fid = footholds[fid]['next']
    assert len(seen) == 30 and footholds[1]['x1'] == 0 and footholds[30]['x2'] == 2700
    portals = root.findall("imgdir[@name='portal']/imgdir")
    assert len(portals) == 1
    spawn = values(portals[0])
    assert spawn['pn'] == 'sp' and spawn['pt'] == '0'
    assert any(f['x1'] <= int(spawn['x']) <= f['x2'] and int(spawn['y']) <= f['y1'] for f in footholds.values())
    art = {}
    for kind in ('brownBrick', 'guild', 'skyStation'):
        art[kind] = {r['path']: r for r in json.loads((palette / kind / 'index.json').read_text(encoding='utf-8'))}
    refs = 0
    for layer in root.findall('imgdir'):
        if not layer.attrib['name'].isdigit():
            continue
        for t in layer.findall("imgdir[@name='tile']/imgdir"):
            d = values(t)
            r = art['brownBrick'][f"/{d['u']}/{d['no']}"]
            assert (palette / 'brownBrick' / r['file']).is_file()
            refs += 1
        for o in layer.findall("imgdir[@name='obj']/imgdir"):
            d = values(o)
            r = art[d['oS']][f"/{d['l0']}/{d['l1']}/{d['l2']}/0"]
            assert (palette / d['oS'] / r['file']).is_file()
            refs += 1
    for b in root.findall("imgdir[@name='back']/imgdir"):
        d = values(b)
        assert f"/back/{d['no']}" in art[d['bS']]
    return {'footholds': len(footholds), 'continuousWalkway': [0, 2700], 'resolvedTileObjectReferences': refs,
            'spawnSupported': True, 'sourceImgRoundtrip': 'checked by FloorMap',
            'runtimeAssetReferences': 'not tested against client NX', 'gameTested': False, 'installed': False}


candidate = Path(sys.argv[1])
palette = candidate.parent / 'palette'
xml = next(candidate.glob('*.img.xml'))
root = ET.parse(xml).getroot()
report = check(root, palette)
# Regression: a deliberately broken adjacency must be rejected.
broken = copy.deepcopy(root)
broken.find("imgdir[@name='foothold']/imgdir/imgdir/imgdir/int[@name='next']").set('value', '99999')
try:
    check(broken, palette)
except (AssertionError, KeyError):
    report['brokenLinkNegativeTest'] = 'passed'
else:
    raise AssertionError('Validator accepted a broken link')
(candidate / 'validation.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
