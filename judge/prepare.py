import glob, json, os
sdk = sorted(glob.glob('/usr/share/dotnet/sdk/10.*'))[-1]
refs = sorted(glob.glob('/usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/10.*/ref/net10.0'))[-1]
os.symlink(sdk + '/Roslyn/bincore', '/opt/judge/roslyn')
with open('/opt/judge/references.rsp', 'w') as f:
    f.write('\n'.join('-r:' + path for path in sorted(glob.glob(refs + '/*.dll'))))
with open('/opt/judge/Solution.runtimeconfig.json', 'w') as f:
    json.dump({'runtimeOptions': {'tfm': 'net10.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '10.0.0'}}}, f)
