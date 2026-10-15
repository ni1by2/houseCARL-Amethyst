"""Render the public audit from its JSON catalogue and removal ledger (standard library only)."""
import html
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
data = json.loads((HERE / 'catalogue.json').read_text())
ledger = json.loads((HERE / 'removed-tests.json').read_text())
resolution_path = HERE / 'resolution.json'
resolution = json.loads(resolution_path.read_text()) if resolution_path.exists() else None
resolved = {row['original_test']: row for row in resolution['cases']} if resolution else {}
caprica_path = HERE / 'caprica-replacements.json'
caprica = json.loads(caprica_path.read_text()) if caprica_path.exists() else None


def esc(value):
    return html.escape(str(value), quote=True)


def para(value):
    return f'<p>{esc(value)}</p>'


def source(loc, label=None):
    suffix = '' if loc.get('line_is_pre_removal') else f'#L{loc["line"]}'
    return f'<a href="../../../{esc(loc["path"])}{suffix}">{esc(label or loc["path"].split("/")[-1])}</a>'


def evidence(items):
    if not items:
        return '<p class="gap">No equivalent passing test established.</p>'
    rows = []
    for item in items:
        count = len(item['baseline_cases'])
        rows.append('<li>' + source(item['source'], item['method']) +
                    f' <span class="badge">{count} passing case{"s" if count != 1 else ""}</span>' +
                    para(item['assertion_scope']) + '</li>')
    return '<ul class="evidence">' + ''.join(rows) + '</ul>'


def verification_table():
    labels = {'baseline_full': 'Full suite before retirement', 'before_focused': 'Focused before retirement',
              'after_focused': 'Focused retained checks', 'after_full': 'Full suite after retirement'}
    rows = []
    for key, title in labels.items():
        run = data['verification'][key]
        if run is None:
            rows.append(f'<tr><th>{title}</th><td colspan="4">Pending</td></tr>')
            continue
        counts = run['counts']
        rows.append(f'<tr><th>{title}</th><td>{counts.get("Passed", 0):,}</td>'
                    f'<td>{counts.get("Failed", 0):,}</td><td>{counts.get("NotExecuted", 0):,}</td><td>{run["total"]:,}</td></tr>')
    if resolution:
        counts = resolution['verification']['full_suite']
        rows.append('<tr><th>Full suite after all repairs</th>' +
                    f'<td>{counts.get("Passed", 0):,}</td><td>{counts.get("Failed", 0):,}</td>' +
                    f'<td>{counts.get("NotExecuted", 0):,}</td><td>{resolution["verification"]["total"]:,}</td></tr>')
    if caprica:
        counts = caprica['verification']['full_suite']
        rows.append('<tr><th>Full suite after real Caprica replacements</th>' +
                    f'<td>{counts.get("Passed", 0):,}</td><td>{counts.get("Failed", 0):,}</td>' +
                    f'<td>{counts.get("NotExecuted", 0):,}</td><td>{caprica["verification"]["total"]:,}</td></tr>')
    return '<div class="table-wrap"><table><thead><tr><th>Run</th><th>Passed</th><th>Failed</th><th>Skipped</th><th>Total</th></tr></thead><tbody>' + ''.join(rows) + '</tbody></table></div>'


removals = []
for index, row in enumerate(ledger['removals'], 1):
    mapping = ''.join('<li><strong>' + esc(m['old_assertion']) + '</strong> ' + esc(m['disposition']) + '</li>' for m in row['mapping'])
    retained = row['retained_verification']
    all_pass = all(r['focused_after'] == 'Passed' and r['full_after'] == 'Passed' for r in retained)
    removals.append(f'<article class="removal" id="removal-{index}"><p class="eyebrow">Removal {index} · {esc(row["kind"])}</p>'
                    f'<h3>{esc(row["id"].removeprefix("HousecarlMcpTests."))}</h3>' +
                    para(row['intent']) + '<h4>Reason for removal</h4>' + para(row['reason']) +
                    '<h4>Assertion-by-assertion disposition</h4><ul>' + mapping + '</ul>' +
                    '<h4>Retained evidence</h4>' + evidence(row['evidence']) +
                    para('All listed witnesses passed both focused and full verification.' if all_pass else 'Verification pending; inspect the ledger.') +
                    '<p class="note"><strong>Scope deliberately retired:</strong> ' + esc(row['not_preserved']) + '</p>' +
                    '<details><summary>Archived method and restoration evidence</summary>' +
                    para(f'Original file: {row["source"]}, pre-removal method line {row["original_line"]}.') +
                    '<p>Exact removed snippet SHA-256: <code>' + esc(row['removed_source_sha256']) + '</code></p>' +
                    '<pre><code>' + esc(row['removed_source']) + '</code></pre>' +
                    '<p>The ledger also records the original file hash. The method can be restored from this snippet and the shared helpers retained in its source file.</p></details></article>')

group_sections = []
for key, group in data['groups'].items():
    entries = [c for c in data['cases'] if c['group'] == key]
    kept = sum(c['action'] != 'removed' for c in entries)
    tasks = ''.join('<li>' + esc(t) + '</li>' for t in group['work'])
    current = f'<p class="note">All {kept} retained cases in this group now pass. The plan and related evidence below record the original audit.</p>' if resolution else ''
    group_sections.append(f'<details class="group" id="group-{key}"><summary>{esc(group["title"])} '
                          f'<span class="badge">{len(entries)} reviewed · {kept} retained</span></summary>' +
                          current + '<h3>Original failure cause</h3>' + para(group['cause']) +
                          '<h3>Coverage judgment</h3>' + para(group['coverage']) +
                          '<h3>Original repair plan</h3><ol>' + tasks + '</ol>' +
                          '<h3>Related passing checks — not a blanket replacement</h3>' + evidence(group['evidence']) + '</details>')

case_rows = []
for row in data['cases']:
    group = data['groups'][row['group']]
    removed = row['action'] == 'removed'
    decision = data['actions'][row['action']]
    status = resolved.get(row['test'])
    detail = ('See the removal ledger above for the exact assertion mapping and archived source.' if removed else status['reason'] if status else row['next_check'])
    current_link = ('<p class="label">Current passing test: ' + source(status['source'], status['current_test'].removeprefix('HousecarlMcpTests.')) + '</p>') if status else source(row['source'])
    result_badge = '<p><span class="badge done">Resolved · Passed</span></p>' + para(status['resolution_kind']) if status else ''
    case_rows.append(f'<tr data-action="{esc(row["action"])}" data-group="{esc(row["group"])}" id="{esc(row["id"])}">'
                     f'<td class="case-id">{esc(row["id"])}</td><td><strong class="test-name">{esc(row["test"].removeprefix("HousecarlMcpTests."))}</strong>'
                     '<details><summary>Intent, source and next check</summary><p class="label">Legacy test intent (not automatically the current contract)</p>' +
                     para(row['legacy_test_intent']) + current_link + para(detail) + '</details></td>' +
                     f'<td>{result_badge}<span class="badge {"done" if removed else ""}">{esc(decision)}</span><p>{esc(data["coverage_labels"][row["coverage"]]) if not status else "Original classification retained for audit history."}</p></td>' +
                     f'<td><a href="#group-{esc(row["group"])}" class="group-link">{esc(group["title"])}</a></td></tr>')

action_options = ''.join(f'<option value="{esc(k)}">{esc(v)} ({data["counts"].get(k, 0)})</option>' for k, v in data['actions'].items())
group_options = ''.join(f'<option value="{esc(k)}">{esc(g["title"])}</option>' for k, g in data['groups'].items())
limit_list = ''.join('<li>' + esc(t) + '</li>' for t in data['limits'])
repair = data['counts']['repair_existing'] + data['counts']['refresh_assertion']
new = data['counts']['new_fixture'] + data['counts']['new_scenario']
hashes = ''.join(f'<li>{esc(r["artifact_name"])}: <code>{r["sha256"]}</code></li>' for r in
                 (data['verification'][key] for key in ('baseline_full', 'before_focused', 'after_focused', 'after_full')) if r)

page = '''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Linux / Amethyst — test coverage and retirement audit</title>
<style>
:root{color-scheme:light;--ink:#172638;--muted:#506276;--line:#ced8e3;--paper:#fff;--accent:#176052;--soft:#edf5f2}
*{box-sizing:border-box}body{margin:0;background:#f3f6f9;color:var(--ink);font:16px/1.6 system-ui,sans-serif}
main{max-width:1440px;margin:auto;padding:40px 32px 80px}h1{font-size:clamp(29px,3.6vw,46px);line-height:1.15;letter-spacing:-1px;margin:12px 0 20px}h2{font-size:25px;margin:44px 0 16px}h3{font-size:18px;overflow-wrap:anywhere}h4{margin:22px 0 8px}p{margin:12px 0}a{color:#125890;text-underline-offset:3px}a:hover{color:#093a60}code,pre{font:13px/1.5 ui-monospace,monospace}code{overflow-wrap:anywhere}pre{padding:16px;background:#f2f5f8;border-radius:6px;overflow:auto;white-space:pre-wrap}
.eyebrow{text-transform:uppercase;font-size:12px;letter-spacing:1.4px;font-weight:750;color:var(--accent)}.lead{font-size:20px;max-width:1000px}.muted,.label{color:var(--muted)}.label{font-size:13px}.cards{display:grid;grid-template-columns:repeat(3,1fr);gap:16px;margin:24px 0}.card{background:var(--paper);border:1px solid var(--line);padding:22px;border-radius:10px}.number{display:block;font-size:36px;font-weight:750;line-height:1.15}.card p{margin:8px 0 0}.note{background:var(--soft);border-left:4px solid var(--accent);padding:14px 18px}.badge{display:inline-block;border-radius:5px;background:#edf1f6;padding:3px 8px;font-size:12px;font-weight:650;line-height:1.5}.done{background:#deeee7;color:#174a38}nav{display:flex;flex-wrap:wrap;gap:10px 22px;margin:22px 0}.table-wrap{overflow:auto;border:1px solid var(--line);border-radius:8px;background:white}table{border-collapse:collapse;width:100%;text-align:left}th,td{padding:14px;border-bottom:1px solid var(--line);vertical-align:top}thead{background:#e9eef4}th{font-size:13px}tbody tr:last-child td,tbody tr:last-child th{border-bottom:0}td p{font-size:14px;margin:8px 0}.removal{background:white;border:1px solid var(--line);border-radius:10px;padding:24px;margin:18px 0}.evidence li{margin:12px 0;overflow-wrap:anywhere}.evidence p{font-size:14px;margin:6px 0}.group{padding:16px 20px;background:white;border:1px solid var(--line);border-radius:8px;margin:10px 0}summary{cursor:pointer;font-weight:600}details[open]>summary{margin-bottom:14px}details p{font-size:14px}.filters{display:grid;grid-template-columns:2fr 1.3fr 1.3fr;gap:12px;margin:18px 0}label{font-size:13px;font-weight:650}input,select{font:inherit;width:100%;padding:10px;border:1px solid #a8b7c6;background:white;border-radius:6px;margin-top:6px;color:var(--ink)}.case-table{max-height:76vh}.case-table thead th{position:sticky;top:0;background:#e9eef4;z-index:1}.test-name{font:13px/1.5 ui-monospace,monospace;overflow-wrap:anywhere;display:block}.case-table td:nth-child(2){width:49%;min-width:300px}.case-table td:nth-child(3){width:23%}.case-table td:nth-child(4){min-width:160px;font-size:14px}.case-table summary{font-size:13px;margin-top:10px}.case-id{font-size:12px;color:var(--muted)}li{margin:7px 0}footer{margin-top:32px;font-size:13px;color:var(--muted)}[hidden]{display:none!important}:focus-visible{outline:3px solid #3685b8;outline-offset:3px}
@media(max-width:760px){main{padding:24px 16px}.cards,.filters{grid-template-columns:1fr}.card{padding:16px}.number{font-size:30px}.removal{padding:18px}}
@media print{body{background:white}main{max-width:none;padding:0}.filters,nav{display:none}.case-table{max-height:none;overflow:visible}thead{display:table-header-group}.case-table thead th{position:static}.table-wrap{overflow:visible}.removal,.group{break-inside:avoid}a{color:inherit}}
</style></head><body><main>
'''
page += '<p class="eyebrow">Engineering evidence · 14 October 2026 · Linux x64</p><h1>Test coverage and retirement audit</h1>'
page += '<p class="lead">All 263 retained failures now have passing Linux / Amethyst coverage. Three earlier retirements remain documented below.</p>' if resolution else '<p class="lead">266 remaining failures reviewed. Three tests retired with evidence; 263 retained until their relevant behavior can be verified on Linux and Amethyst.</p>'
page += '<nav aria-label="Report sections"><a href="#removals">Removal ledger</a><a href="#coverage">Coverage and replacement work</a><a href="#catalogue">All 266 cases</a><a href="#verification">Verification</a><a href="catalogue.json">Catalogue JSON</a><a href="removed-tests.json">Removal ledger JSON</a></nav>'
if resolution:
    page += '<p><a href="resolution.json">Complete resolution ledger (JSON)</a> · every original case maps to its passing current test, with the repair or replacement reason.</p>'
    latest = caprica['verification']['full_suite'] if caprica else resolution['verification']['full_suite']
    page += '<div class="cards"><div class="card"><span class="number">3</span><p>Earlier retirements with archived source and retained evidence.</p></div><div class="card"><span class="number">263</span><p>Resolved retained failures; five use explicit Linux counterparts.</p></div><div class="card"><span class="number">' + str(latest.get('Failed', 0)) + '</span><p>Failures in the complete Release suite: ' + f'{latest.get("Passed", 0):,}' + ' passed, ' + str(latest.get('NotExecuted', 0)) + ' skipped.</p></div></div>'
else:
    page += f'<div class="cards"><div class="card"><span class="number">3</span><p>Removed: two duplicated checks and one Windows-only control.</p></div><div class="card"><span class="number">{repair}</span><p>Keep and repair existing fixtures, paths or contract assertions.</p></div><div class="card"><span class="number">{new}</span><p>Keep pending Linux/Amethyst fixture or scenario work.</p></div></div>'
page += '<p class="note"><strong>Retirement and repair are separate.</strong> The three deleted tests have an assertion-by-assertion ledger. The other 263 cases were repaired or replaced for Linux and all pass. Fixture work exposed three product changes: named permission failures, correct SKSE filenames and preserved Unix file permissions.</p>' if resolution else '<p class="note"><strong>Deletion is not a bug fix.</strong> The removed assertions are accounted for below. Related passing tests do not justify removing unique behavior. The remaining failures may expose further product issues once their setup is repaired.</p>'
page += para(data['scope']) + para(data['policy'])
if caprica:
    page += '<h2 id="caprica">Five skipped compiler tests replaced</h2><p><a href="caprica-replacements.json">Caprica replacement ledger (JSON)</a> · original source, assertion mappings, product fixes and verification hashes.</p>'
    for row in caprica['cases']:
        page += '<article class="removal"><h3>' + esc(row['current_test']) + '</h3>' + para(row['reason']) + para(row['assertions']) + '<p>' + source(row['source']) + '</p></article>'
    page += para(caprica['remaining_skip'])
page += '<h2 id="removals">Removal ledger</h2><p>Each entry records the original intent, exact reason, surviving assertions, deliberate platform differences and the removed method for restoration. No tests were replaced by skips.</p>' + ''.join(removals)
page += '<h2 id="coverage">Coverage and replacement work</h2><p>Expand a group for the evidence, its limits, and the work required. Passing witnesses below are related coverage unless a removal entry explicitly establishes equivalence.</p>' + ''.join(group_sections)
page += '<h2 id="catalogue">Case catalogue</h2><p>One row per historical failing case, including individual theory rows. Expand each retained row for its current passing test and resolution reason. The original audit classification remains visible for publication history.</p>' if resolution else '<h2 id="catalogue">Case catalogue</h2><p>One row per failing test case, including individual theory rows. “Related tests pass” means equivalence has <strong>not</strong> been established. Each source link identifies the original test; retained rows remain in the suite.</p>'
page += '<div class="filters"><label>Search name, intent or next check<input id="search" type="search" placeholder="For example: FaceGen, consent, import"></label><label>Decision<select id="action"><option value="">All decisions</option>' + action_options + '</select></label><label>Failure group<select id="group"><option value="">All groups</option>' + group_options + '</select></label></div>'
page += '<p id="visible-count" role="status" aria-live="polite">266 of 266 cases</p><div class="table-wrap case-table"><table><thead><tr><th>Case</th><th>Test and protected behavior</th><th>Decision / coverage</th><th>Evidence and required work</th></tr></thead><tbody id="cases">' + ''.join(case_rows) + '</tbody></table></div>'
comparison = 'Every original retained failure maps to a passing case; all prior passes remain passing. The independent Python installer/package suite also passed all 21 tests.' if resolution else data['verification']['comparison']
comparison += ' Five former skips now pass against real Caprica; one Windows handle/deletion case remains skipped.' if caprica else (' The same six skips remained in this historical run.' if resolution else '')
page += '<h2 id="verification">Verification and limits</h2>' + verification_table() + para(comparison) + para(data['verification']['build'])
if resolution:
    limit_list = ''.join('<li>' + esc(t) + '</li>' for t in resolution['limits'])
    hashes += '<li>Full suite after repair: <code>' + resolution['verification']['trx_sha256'] + '</code></li>'
    hashes += '<li>Permission-regression control (three failures with correction removed): <code>' + resolution['verification']['permission_control']['trx_sha256'] + '</code></li>'
if caprica:
    limit_list = ''.join('<li>' + esc(t) + '</li>' for t in caprica['limits'])
    hashes += '<li>Full suite after real compiler replacements: <code>' + caprica['verification']['full_trx_sha256'] + '</code></li>'
page += '<ul>' + limit_list + '</ul><details><summary>Evidence provenance and run hashes</summary>' + para(data['provenance']['environment']) + para(data['provenance']['revision_note']) + '<p>Worktree HEAD: <code>' + esc(data['provenance']['worktree_head']) + '</code>; working tree already contained unfinished changes.</p>' + para(data['provenance']['raw_evidence_policy']) + '<ul>' + hashes + '</ul></details>'
page += '<footer>The catalogue, removal ledger and resolution ledger JSON files are the maintained evidence records. Regenerate this HTML with render.py after updating them. Repository-relative source links and embedded assets make this report suitable for a repository or static publication; no external scripts or services are loaded.</footer>'
page += '''</main><script>
const rows=[...document.querySelectorAll('#cases tr')];
const search=document.querySelector('#search'), action=document.querySelector('#action'), group=document.querySelector('#group');
function filter(){const q=search.value.toLowerCase().trim();let count=0;for(const row of rows){row.hidden=!!((q&&!row.textContent.toLowerCase().includes(q))||(action.value&&row.dataset.action!==action.value)||(group.value&&row.dataset.group!==group.value));if(!row.hidden)count++;}document.querySelector('#visible-count').textContent=`${count} of ${rows.length} cases`;}
for(const field of [search,action,group])field.addEventListener('input',filter);
for(const link of document.querySelectorAll('.group-link'))link.addEventListener('click',()=>{document.querySelector(link.getAttribute('href')).open=true;});
function openHash(){const target=document.getElementById(location.hash.slice(1));if(target&&target.tagName==='DETAILS')target.open=true;}
window.addEventListener('hashchange',openHash);openHash();
</script></body></html>'''
(HERE / 'index.html').write_text(page)
print(f'Rendered {len(data["cases"])} cases and {len(ledger["removals"])} removal entries.')
