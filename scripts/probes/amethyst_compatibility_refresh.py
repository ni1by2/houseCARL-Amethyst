"""Exercise manager-owned filegraph refreshes in a separate disposable profile."""
import json
import os
from pathlib import Path
import sys
import tempfile


class RefreshCheckFailed(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise RefreshCheckFailed(message)


def check_transitions(library, profile, expected, read_version, write_version):
    """Verify marker transitions against the producer, restoring instrumentation afterward."""
    require(read_version(profile) == expected, 'initial refresh did not save the current filegraph version')
    library_type = type(library)
    original = library_type.refresh_changed
    calls = []

    def counted(self, *args, **kwargs):
        if self is library:
            calls.append(1)
        return original(self, *args, **kwargs)

    checks = ['refresh_records_current_version']
    library_type.refresh_changed = counted
    try:
        library.ensure_ready(profile)
        require(not calls, 'matching filegraph version caused an unnecessary refresh')
        checks.append('matching_version_no_refresh')

        state_path = profile / 'profile_state.json'
        state = json.loads(state_path.read_text())
        state.pop('filegraph_version', None)
        state_path.write_text(json.dumps(state))
        library.ensure_ready(profile)
        require(len(calls) == 1 and read_version(profile) == expected,
                'missing filegraph version did not refresh once and save the current version')
        checks.append('missing_version_refreshes_once')

        library.ensure_ready(profile)
        require(len(calls) == 1, 'refresh repeated after repairing the missing version')
        checks.append('repaired_missing_version_no_repeat_refresh')

        write_version(profile, dict(expected, rules=expected['rules'] - 1))
        library.ensure_ready(profile)
        require(len(calls) == 2 and read_version(profile) == expected,
                'old filegraph version did not refresh once and save the current version')
        checks.append('old_version_refreshes_once')

        library.ensure_ready(profile)
        require(len(calls) == 2, 'refresh repeated after repairing the old version')
        checks.append('repaired_old_version_no_repeat_refresh')
    finally:
        library_type.refresh_changed = original
    return {'checks': checks, 'refresh_calls': len(calls)}


def probe(extension, source, server):
    os.environ['AMETHYST_FILEGRAPH_EXTENSION'] = str(extension)
    sys.path.insert(0, str(source / 'src'))
    from Utils.profiles import state

    readers = [getattr(state, name, None) for name in ('read_filegraph_version', 'write_filegraph_version')]
    if all(item is None for item in readers):
        return {'status': 'not_applicable', 'reason': 'producer does not expose the filegraph-version marker API; transitions not tested'}
    if not all(callable(item) for item in readers):
        raise ValueError('incomplete filegraph-version marker API; update the fixture driver')

    from amethyst_compatibility_fixture import produce
    from amethyst_compatibility_worker import server_probe
    from build_amethyst_snapshot_fixture import FixtureGame
    from Utils.filegraph.service import LibrarySession

    with tempfile.TemporaryDirectory(prefix='hc-refresh-lifecycle-') as raw:
        root = Path(raw)
        oracle = produce(root, extension, source)
        expected = {key: oracle['contract'][key] for key in ('schema', 'engine', 'rules')}
        profile = root / 'profiles/default'
        library = LibrarySession(FixtureGame(root), root)
        result = check_transitions(library, profile, expected, *readers)
        session = library.open_profile(profile)
        session.reconcile(operation_hint={'kind': 'full'})
        require(not session.deployment_unchanged(session.snapshot().generation, 'hardlink'),
                'automatic catalog refresh incorrectly verified an uncommitted deployment')
        result['checks'].append('uncommitted_deployment_stays_unverified')
        try:
            runtime = server_probe(root, server)
        except ValueError as exc:
            raise RefreshCheckFailed('post-refresh MCP check failed: ' + str(exc)) from exc
        require(runtime['status'] in ('accepted', 'blocked_by_contract'), 'unexpected post-refresh MCP result')
        if runtime['status'] == 'accepted':
            result['checks'].append('runtime_accepts_post_refresh_snapshot')
        result.update(status='pass', contract=oracle['contract'], production_server=runtime)
        return result


def main():
    extension, source, server, output = map(Path, sys.argv[1:])
    try:
        result = probe(extension, source, server)
    except RefreshCheckFailed as exc:
        result = {'status': 'fail', 'error': str(exc)}
    except Exception as exc:
        result = {'status': 'setup_error', 'error': str(exc)}
    result['scope'] = 'separate synthetic profile; intentional manager refreshes, no live profile or deployment'
    output.write_text(json.dumps(result, indent=2) + '\n')
    return {'pass': 0, 'not_applicable': 0, 'fail': 1, 'setup_error': 2}[result['status']]


if __name__ == '__main__':
    sys.exit(main())
