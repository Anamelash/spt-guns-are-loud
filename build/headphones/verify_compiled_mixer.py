"""Compare original stock DSP graph with a generated candidate by persistent GUID.

Run with uv run --with UnityPy python ... ORIGINAL_JSON CANDIDATE_BUNDLE.
Checks native compiled DSP data, not only exposed editor properties.
"""
import json
import sys
from pathlib import Path
import UnityPy

original = json.loads(Path(sys.argv[1]).read_text())['m_MixerConstant']
env = UnityPy.load(sys.argv[2])
candidate = next(o.read_typetree()['m_MixerConstant'] for o in env.objects
                 if o.type.name == 'AudioMixerController')
def guid(g): return tuple(g.values())
def table(d, key): return {guid(g): i for i,g in enumerate(d[key+'GUIDs'])}
def names(d): return bytes(d['groupNameBuffer']).decode().strip('\0').split('\0')
def value(d, ix):
    if ix == 0xffffffff: return None
    return {s['nameHash']: s['values'][ix] for s in d['snapshots']}
def ref(d, key, ix): return None if ix in (-1,0xffffffff) else guid(d[key+'GUIDs'][ix])
def parameter_owners(d):
    owners={}
    for i,g in enumerate(d['groups']):
        for key in ('volumeIndex','pitchIndex'):
            owners[g[key]]=('group',ref(d,'group',i),key)
    for i,e in enumerate(d['effects']):
        for slot,index in enumerate(e['parameterIndices']):
            owners[index]=('effect',ref(d,'effect',i),slot)
        if e['wetMixLevelIndex']!=0xffffffff:
            owners[e['wetMixLevelIndex']]=('effect',ref(d,'effect',i),'wet')
    return owners
def normalized_group(d, i):
    g=d['groups'][i]
    return {k: (value(d,v) if k in ('volumeIndex','pitchIndex') else
                ref(d,'group',v) if k=='parentConstantIndex' else v) for k,v in g.items()}
def normalized_effect(d,i):
    e=d['effects'][i]
    return {k: ([value(d,x) for x in v] if k=='parameterIndices' else
                value(d,v) if k=='wetMixLevelIndex' else
                ref(d,'effect',v) if k in ('sendTargetEffectIndex','prevEffectIndex') else
                ref(d,'group',v) if k=='groupConstantIndex' else v) for k,v in e.items()}
errors=[]
old_groups=table(original,'group'); new_groups=table(candidate,'group')
new_names=names(candidate); old_names=names(original)
passive_ids=[g for g,i in new_groups.items() if new_names[i]=='GAL Passive']
assert len(passive_ids)==1, 'Exactly one GAL Passive group required'
passive=passive_ids[0]
for g,i in old_groups.items():
    if g not in new_groups: errors.append(('missing-group',old_names[i])); continue
    a=normalized_group(original,i); b=normalized_group(candidate,new_groups[g])
    if b['parentConstantIndex']==passive:
        assert old_names[i] in ('NonspatialBypass','Guns','Main','Occlusion')
        b['parentConstantIndex']=a['parentConstantIndex']
    if a!=b: errors.append(('group',old_names[i],a,b))
new_effects=table(candidate,'effect')
original_effects=table(original,'effect')
for g,i in table(original,'effect').items():
    if g not in new_effects: errors.append(('missing-effect',i)); continue
    a=normalized_effect(original,i); b=normalized_effect(candidate,new_effects[g])
    # This is the serialized effect-list link (including effects of other
    # groups), not a send target. Remove only newly inserted list nodes.
    while b['prevEffectIndex'] is not None and b['prevEffectIndex'] not in original_effects:
        previous = candidate['effects'][new_effects[b['prevEffectIndex']]]
        b['prevEffectIndex'] = ref(candidate,'effect',previous['prevEffectIndex'])
    if a!=b: errors.append(('effect',i,a,b))
old_exposed=dict(zip(original['exposedParameterNames'],original['exposedParameterIndices']))
new_exposed=dict(zip(candidate['exposedParameterNames'],candidate['exposedParameterIndices']))
old_owners=parameter_owners(original); new_owners=parameter_owners(candidate)
for h,i in old_exposed.items():
    if h not in new_exposed or value(original,i)!=value(candidate,new_exposed[h]):
        errors.append(('exposed',h))
    elif old_owners.get(i)!=new_owners.get(new_exposed[h]):
        errors.append(('exposed-binding',h,old_owners.get(i),new_owners.get(new_exposed[h])))
passive_index = new_groups[passive]
electronics_index = new_names.index('GAL Electronics')
effects = candidate['effects']
passive_effects = [e for e in effects if e['groupConstantIndex']==passive_index]
electronic_effects = [(i,e) for i,e in enumerate(effects) if e['groupConstantIndex']==electronics_index]
assert sorted(e['type'] for e in passive_effects)==[-2]+[10]*9, 'Passive DSP chain incomplete'
assert len(electronic_effects)==3, 'Expected receive, native electronics, attenuation'
receivers = [i for i,e in electronic_effects if e['type']==-4]
assert len(receivers)==1, 'Exactly one shared electronics receive required'
sends = [(i,e) for i,e in enumerate(effects) if e['type']==-3 and e['sendTargetEffectIndex']==receivers[0]]
assert len(sends)==12, 'All twelve world category feeds required'
assert {new_names[e['groupConstantIndex']] for _,e in sends}=={
    'Guns','ClientPlayer','ObservedPlayer','NPC','TechnicalSounds','NatureSounds',
    'CommonSounds','Ambient','Returns','NonspatialBypass','Voip','Occlusion'}
for _,send in sends:
    assert set(value(candidate,send['wetMixLevelIndex']).values())=={-80}, 'New send not silent in Vanilla'
assert set(value(candidate,candidate['groups'][electronics_index]['volumeIndex']).values())=={-80}
assert set(value(candidate,candidate['groups'][passive_index]['volumeIndex']).values())=={0}
native = [e for _,e in electronic_effects if e['type']>=1000]
assert len(native)==1 and len(native[0]['parameterIndices'])==12
for slot in (10,11):
    assert set(value(candidate,native[0]['parameterIndices'][slot]).values())=={0}, 'Wet/reset not neutral'
report={'originalGroups':len(old_groups),'originalEffects':len(original['effects']),
        'originalExposed':len(old_exposed),'candidateEffects':len(candidate['effects']),
        'electronicsFeeds':len(sends),
        'differences':errors}
print(json.dumps(report,indent=2))
sys.exit(bool(errors))
