"""Assemble the research index; validate arrays without inventing missing data."""
import json, hashlib, re, csv, math
from pathlib import Path
P = Path(__file__).parent
inventory = json.loads((P/'inventory.json').read_text(encoding='utf-8'))
families = [
 ('comtac-ii','ComTac II','peltor','MT15H69FB: archived manual has a separately labelled ComTac II ANSI table.'),
 ('tactical-sport','Tactical Sport','peltor','SportTac data require SKU matching to game Tactical Sport.'),
 ('comtac-iv','ComTac IV','peltor','Hybrid in-ear tips: select UltraFit or Torque explicitly; game tip unspecified.'),
 ('comtac-v','ComTac V','peltor','US V and EMEA XPI must remain separate; TW EXFIL is not automatically headband certification.'),
 ('comtac-vi','ComTac VI','peltor','Select regional VI/NIB revision, cushion and mount; TW EXFIL mount requires separate applicability.'),
 ('tep-300','TEP-300','peltor','Eartip and regional test method required; game tip unspecified.'),
 ('sordin','Sordin','tactical','Game MSA PRO-X/L and modern SordinHEAR2 are not an exact revision match.'),
 ('rac','FAST RAC','tactical','RAC headset-only; NFMI combined protection is a different configuration.'),
 ('amp','AMP Communication','tactical','Mod describes rail-mounted AMP; game weight suggests connectorized rail but does not prove SKU; NFMI not specified.'),
 ('liberator','Liberator','tactical','HP 2.0, not rechargeable HP-R; identify electronic mode and suspension.'),
 ('gssh','GSSh','tactical','Secondary claims only until a primary passport is verified; excluded from automatic numeric selection.'),
 ('razor','Razor','consumer','Exact Digital SKU unresolved; analog Slim, Digital BT and X-TRM remain separate.'),
 ('xcel','XCEL','consumer','XCEL 500BT; do not transfer Razor response timing.'),
 ('m32','M32','consumer','Game revision unspecified; MOD3, Plus, Mark4 must remain separate; placeholder website claims quarantined.'),
 ('cens','CENS','consumer','DX5 is explicit; ProFlex family passive data require configuration assessment; custom fit varies.')]
models=[]
for fid,match,pack,note in families:
    entries=[r for r in inventory['items'] if re.search(re.escape(match)+r'\b', r['name'], re.I)]
    assert entries, fid
    models.append({'familyId':fid,'gameFamilyName':match,'researchFile':f'research-{pack}.json',
        'humanReference':f'research-{pack}.md','applicabilityNote':note,
        'runtimeFallbackProfile': 'rac-proposed' if fid == 'amp' else None,
        'selectedCalculationVariant':None,'automaticApplicationAllowed':False,
        'items':[{k:r[k] for k in ['templateId','name','origin','sourceFile','cloneTemplateId','realisticProfileRegistered']} for r in entries]})
ids=[r['templateId'] for m in models for r in m['items']]
assert len(ids)==len(set(ids))==len(inventory['items'])==29
sources=[]; table_count=0; band_rows=[]; arithmetic_notes=[]
def check(node,path='root',inherited_freq=None):
    global table_count
    if isinstance(node,dict):
        freq=next((v for k,v in node.items() if k in ('frequencies_hz','frequenciesHz','frequency_hz','frequencyHz') and isinstance(v,list)),inherited_freq)
        if freq is not None and any(k in node for k in ('mean_db','meanDb','mean_dbA','mean_attenuation_db')):
            assert all(isinstance(f,(int,float)) and f>0 for f in freq),path
            assert freq==sorted(set(freq)),path
            for k,v in node.items():
                if k in ('mean_db','sd_db','apv_db','meanDb','sdDb','apvDb','mean_dbA','sd_dbA','mean_attenuation_db','standard_deviation_db','assumed_protection_db') and v is not None:
                    assert len(v)==len(freq),(path,k,len(v),len(freq))
                    assert all(x is None or isinstance(x,(int,float)) and math.isfinite(x) for x in v),(path,k)
            table_count+=1
            def series(*keys):
                return next((node[k] for k in keys if node.get(k) is not None), [None]*len(freq))
            means=series('mean_db','meanDb','mean_dbA','mean_attenuation_db')
            deviations=series('sd_db','sdDb','sd_dbA','standard_deviation_db')
            protection=series('apv_db','apvDb','assumed_protection_db')
            for i,f in enumerate(freq):
                if all(v[i] is not None for v in (means,deviations,protection)) and abs(means[i]-deviations[i]-protection[i])>.15:
                    arithmetic_notes.append({'record':path,'frequencyHz':f,'publishedMeanDb':means[i],
                        'publishedSdDb':deviations[i],'publishedApvDb':protection[i],
                        'action':'Preserve source values; review discrepancy before using this APV point.'})
                band_rows.append({'sourcePack':'research-'+path.split('/')[0]+'.json',
                    'recordPointer':'/'+path.split('/',1)[1], 'frequencyHz':f,
                    'meanDb':means[i], 'standardDeviationDb':deviations[i], 'apvDb':protection[i],
                    'automaticApplicationAllowed':False})
        for k,v in node.items():check(v,path+'/'+k,freq)
    elif isinstance(node,list):
        for i,v in enumerate(node):check(v,path+'/'+str(i),inherited_freq)
for pack in ['peltor','tactical','consumer']:
    file=P/f'research-{pack}.json'
    data=json.loads(file.read_text(encoding='utf-8-sig'))
    check(data,pack)
    sources.append({'file':file.name,'sha256':hashlib.sha256(file.read_bytes()).hexdigest()})
reference={'schemaVersion':1,'date':'2026-09-07','purpose':'Evidence index for future sound calibration, not a ready-to-apply DSP preset.',
 'scope':inventory['scope'],'itemCount':len(ids),'physicalFamilyCount':len(models),
 'policy':{'unknownValue':None,'gameValuesAreRealSpecifications':False,'singleNumberRatingIsFrequencyCurve':False,
 'familyTransferRequiresApproximationMarker':True,'pendingVariantOrConflictingSourceBlocksAutomaticSelection':True},
 'sourcePacks':sources,'models':models}
reference['validation']={'attenuationTables':table_count,'frequencyPoints':len(band_rows),'sourceArithmeticNotes':arithmetic_notes}
(P/'reference.json').write_text(json.dumps(reference,ensure_ascii=False,indent=2),encoding='utf-8')
with (P/'attenuation.csv').open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=['sourcePack','recordPointer','frequencyHz','meanDb','standardDeviationDb','apvDb','automaticApplicationAllowed'])
    writer.writeheader(); writer.writerows(band_rows)
lines=['# Перечень наушников в установленной сборке','',
 '29 предметов / 15 моделей и семейств. Базовая база SPT: 15 предметов; WTT-ContentBackport: 10; Epic’s All In One: 4. Цветовые варианты перечислены отдельно. Категория Headphones не считается предметом.','',
 'Поддержка Realistic ниже означает только наличие идентификатора в текущем реестре, не достоверность всех параметров и не подтверждение загрузки DSP в игре.','']
for model in models:
    lines += ['## '+model['gameFamilyName'],'',f"[Реальные характеристики]({model['humanReference']}).",'',
        '| Предмет | TemplateId | Источник | Профиль Realistic |','|---|---|---|---|']
    for row in model['items']:
        lines.append('| '+row['name']+' | `'+row['templateId']+'` | '+row['origin']+' | '+('есть' if row['realisticProfileRegistered'] else 'нет')+' |')
    lines+=['']
lines+=['С версии 0.19.2 все четыре AMP временно используют профиль FAST RAC (rac-proposed) по запросу пользователя. Это fallback, а не результат калибровки AMP; приближённые значения отмечаются звёздочкой. Реальные характеристики AMP и RAC в справочнике остаются раздельными.','']
(P/'inventory.md').write_text('\n'.join(lines),encoding='utf-8')
print(json.dumps({'items':len(ids),'families':len(models),'attenuationTablesChecked':table_count,'sourcePacks':sources},indent=2))
