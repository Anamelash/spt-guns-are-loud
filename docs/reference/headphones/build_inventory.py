"""Read-only inventory of the named SPT instance; never writes game files."""
import json, re, hashlib
from pathlib import Path

ROOT = Path(r'D:\Games\SPT_4.1.3\SPT_Runtime')
OUT = Path(__file__).parent
def read(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))
basepath = ROOT/'SPT_Data/database/templates/items.json'
items = read(basepath)
locale = read(ROOT/'SPT_Data/database/locales/global/en.json')
definitions = {}
errors = []
for p in sorted((ROOT/'user/mods').rglob('*.json')):
    if 'CustomItems' not in p.parts: continue
    try:
        data = read(p)
    except Exception as e:
        errors.append({'file':str(p),'error':str(e)})
        continue
    if not isinstance(data, dict): continue
    for key, value in data.items():
        if isinstance(value, dict) and 'itemTplToClone' in value:
            definitions[key] = (value, p)
modlocales = {}
for p in sorted((ROOT/'user/mods').glob('*/db/CustomLocales/en.json')):
    try: modlocales.update(read(p))
    except Exception as e: errors.append({'file':str(p),'error':str(e)})
def resolve(key, seen=None):
    seen = set() if seen is None else seen
    if key in seen: raise ValueError('clone cycle: '+key)
    seen.add(key)
    if key in definitions:
        d,p = definitions[key]
        parent, props = resolve(d['itemTplToClone'], seen)
        return d.get('parentId',parent), dict(props, **d.get('overrideProperties',{}))
    d = items.get(key,{})
    return d.get('_parent'), d.get('_props',{})
registry = (OUT.parents[2]/'client/GunsAreLoud.Client/Runtime/HeadsetProfileRegistry.cs').read_text(encoding='utf-8-sig')
known = set(re.findall(r'"([0-9a-f]{24})"',registry))
rows=[]
for key in sorted(set(items)|set(definitions)):
    if key not in definitions and items[key].get('_type') != 'Item': continue
    parent,props = resolve(key)
    if parent != '5645bcb74bdc2ded0b8b4578' and 'HeadphonesMixerVolume' not in props: continue
    if key in definitions:
        d,p = definitions[key]
        local = d.get('locales',{}).get('en',{})
        name = local.get('name') or modlocales.get(key+' Name') or props.get('Name') or key
        description = local.get('description') or modlocales.get(key+' Description') or props.get('Description','')
        source = str(p.relative_to(ROOT)); clone = d['itemTplToClone']
        origin=p.relative_to(ROOT/'user/mods').parts[0]
    else:
        source=str(basepath.relative_to(ROOT)); clone=None; origin='SPT base database'
        name=locale.get(key+' Name',props.get('Name',key)); description=locale.get(key+' Description','')
    rows.append({'templateId':key,'name':name,'description':description,'origin':origin,
        'sourceFile':source,'cloneTemplateId':clone,'realisticProfileRegistered':key in known,
        'gameProperties_NOT_realSpecifications':props})
report={'scope':str(ROOT),'date':'2026-09-07','method':'Static base database plus resolved installed CustomItems clone definitions; not a live server dump. Load order/runtime overrides may differ.',
    'baseItemsSHA256':hashlib.sha256(basepath.read_bytes()).hexdigest(),'parseErrors':errors,'items':rows}
(OUT/'inventory.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'count':len(rows),'parseErrors':errors,'items':[{k:r[k] for k in ['templateId','name','origin','realisticProfileRegistered']} for r in rows]},ensure_ascii=False,indent=2))
