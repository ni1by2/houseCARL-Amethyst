#!/usr/bin/env python3
"""Compare a supplied Amethyst producer with supplied readers in an offline disposable container."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from datetime import datetime, timezone


def source_identity(source):
    commit = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
    dirty = subprocess.check_output(['git', '-C', str(source), 'status', '--porcelain', '--untracked-files=all', '--', 'src', 'native'], text=True)
    if dirty:
        raise ValueError('Amethyst src/native must match a clean committed revision')
    return commit


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--producer-extension', required=True, type=Path)
    parser.add_argument('--server', required=True, type=Path, help='self-contained server directory from the baseline release')
    parser.add_argument('--reader', action='append', default=[], metavar='NAME=PATH', help='additional native reader; bundled baseline readers are included')
    parser.add_argument('--output', required=True, type=Path, help='new directory for this immutable comparison report')
    parser.add_argument('--image', default='housecarl-amethyst-compatibility')
    args = parser.parse_args()
    try:
        source, extension, server, output = [p.resolve(strict=True) for p in (args.source, args.producer_extension, args.server)] + [args.output.resolve()]
        if output.exists():
            raise ValueError('output must be a new directory; preserve earlier reports')
        if not extension.is_file() or not (server / 'housecarl-mcp').is_file():
            raise ValueError('producer extension or baseline server is missing')
        commit = source_identity(source)
        readers = [(p.name, p) for p in sorted(server.glob('amethyst_filegraph*.so'))]
        readers += [tuple(value.split('=', 1)) for value in args.reader]
        readers = [(name, Path(path).resolve(strict=True)) for name, path in readers]
        if not readers or len({name for name, _ in readers}) != len(readers):
            raise ValueError('supply at least one reader with unique names')
        repo = Path(__file__).resolve().parents[2]
        output.mkdir(parents=True)
        request = {'report_schema': 1, 'producer_commit': commit, 'producer_extension_sha256': digest(extension),
                   'server_sha256': digest(server / 'housecarl-mcp.dll'), 'helper_sha256': digest(server / 'amethyst_snapshot.py'),
                   'created_at': datetime.now(timezone.utc).isoformat(),
                   'suite_commit': subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip(),
                   'suite_sources': {p.name: digest(p) for p in sorted(Path(__file__).parent.glob('*compatibility*')) if p.is_file()},
                   'readers': [{'name': name, 'sha256': digest(path), 'path': f'/readers/{i}.so'} for i, (name, path) in enumerate(readers)]}
        (output / 'request.json').write_text(json.dumps(request, indent=2) + '\n')
        command = ['docker', 'run', '--rm', '--network=none', '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges',
                   '--user', f'{os.getuid()}:{os.getgid()}', '--tmpfs', '/tmp:rw,exec,mode=1777,size=2g', '--pids-limit=256']
        mounts = [(repo, '/suite', True), (source, '/amethyst', True), (extension, '/producer.so', True),
                  (server, '/server', True), (output, '/output', False)]
        mounts += [(path, f'/readers/{i}.so', True) for i, (_, path) in enumerate(readers)]
        for path, target, readonly in mounts:
            if ',' in str(path):
                raise ValueError('Docker bind paths containing commas are unsupported')
            command += ['--mount', f'type=bind,src={path},dst={target}' + (',readonly' if readonly else '')]
        for key, value in {'HOME': '/tmp/home', 'XDG_CONFIG_HOME': '/tmp/config', 'XDG_STATE_HOME': '/tmp/state',
                           'XDG_DATA_HOME': '/tmp/data', 'XDG_CACHE_HOME': '/tmp/cache', 'PYTHONDONTWRITEBYTECODE': '1'}.items():
            command += ['--env', f'{key}={value}']
        command += [args.image, 'python3', '/suite/scripts/probes/amethyst_compatibility_worker.py']
        with (output / 'container.log').open('w') as log:
            result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
        report_path = output / 'report.json'
        if not report_path.exists():
            report = {'report_schema': 1, 'status': 'setup_error', 'approved': False,
                      'error': 'container did not produce a report; inspect container.log', 'request': request}
            report_path.write_text(json.dumps(report, indent=2) + '\n')
        else:
            report = json.loads(report_path.read_text())
        exit_code = {'comparison_complete': 0, 'comparison_failed': 1, 'setup_error': 2}.get(report['status'], 2)
        if result.returncode != exit_code and report['status'] != 'setup_error':
            report.update(status='setup_error', error='container exit disagrees with report; inspect container.log')
            exit_code = 2
        report['container_exit_code'] = result.returncode
        report_path.write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps({'status': report['status'], 'approved': False, 'report': str(report_path)}))
        return exit_code
    except (ValueError, OSError, subprocess.SubprocessError) as exc:
        parser.exit(2, f'amethyst-compatibility: {exc}\n')


if __name__ == '__main__':
    sys.exit(main())
