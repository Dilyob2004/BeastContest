import base64, glob, json, os, pathlib, sys
paths = glob.glob('/tmp/*.class') if sys.argv[1] == 'Java' else ['/tmp/Solution.dll' if sys.argv[1] == 'C#' else '/tmp/main']
if not 1 <= len(paths) <= 128:
    raise ValueError('Invalid artifact count')
result, total = {}, 0
for name in paths:
    p = pathlib.Path(name)
    if p.is_symlink() or not p.is_file():
        raise ValueError('Invalid artifact')
    total += p.stat().st_size
    if total > 16 * 1024 * 1024:
        raise ValueError('Artifacts exceed 16 MB')
    result[p.name] = base64.b64encode(p.read_bytes()).decode('ascii')
sys.stdout.write(json.dumps(result))
