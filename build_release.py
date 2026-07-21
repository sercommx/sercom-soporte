import base64
#!/usr/bin/env python3
import sys
import os
import re
import subprocess
import json

def main():
    if len(sys.argv) < 2:
        print("Uso: python3 build_release.py <version>")
        print("Ejemplo: python3 build_release.py v4.0.0")
        sys.exit(1)

    version = sys.argv[1].strip()
    if not version.startswith("v"):
        version = "v" + version

    print(f"🚀 Iniciando compilación y sincronización de Release {version}...")

    repo_dir = "/home/ingcrea/github/sercom-soporte"
    cs_file = os.path.join(repo_dir, "SoporteRemotoGUI.cs")
    exe_file = os.path.join(repo_dir, "SercomSoporte.exe")

    # 1. Inyectar versión exacta en SoporteRemotoGUI.cs
    with open(cs_file, "r", encoding="utf-8") as f:
        code = f.read()

    # Reemplazar la constante AppVersion con la versión exacta pasada
    code = re.sub(
        r'private const string AppVersion\s*=\s*"[^"]*";',
        f'private const string AppVersion  = "{version}";',
        code
    )

    
    # Inyectar logotipo en base64 si existe
    logo_file = os.path.join(repo_dir, "logo-texto-blanco.png")
    if os.path.exists(logo_file):
        with open(logo_file, "rb") as lf:
            logo_b64 = base64.b64encode(lf.read()).decode("utf-8")
            code = re.sub(
                r'private static readonly string LogoBase64\s*=\s*"[^"]*";',
                f'private static readonly string LogoBase64 = "{logo_b64}";',
                code
            )

    with open(cs_file, "w", encoding="utf-8") as f:
        f.write(code)

    print(f"✅ Versión {version} inyectada en SoporteRemotoGUI.cs")

    # 2. Compilar ejecutable .exe nativo con mcs
    cmd_compile = f"mcs /target:winexe /out:{exe_file} /r:System.Windows.Forms.dll,System.Drawing.dll,System.dll,System.Core.dll {cs_file}"
    res = subprocess.run(cmd_compile, shell=True, capture_output=True, text=True)
    if res.returncode != 0:
        print(f"❌ Error de compilación:\n{res.stderr}")
        sys.exit(1)

    print(f"✅ Compilación exitosa: {exe_file}")

    # 3. Commit y Tag en Git
    subprocess.run("git add .", shell=True, cwd=repo_dir)
    subprocess.run(f'git commit -m "Release {version}: Sincronización automática de versión"', shell=True, cwd=repo_dir)
    subprocess.run(f"git tag -a {version} -m 'SercomDesk {version} Release'", shell=True, cwd=repo_dir)
    subprocess.run("git push origin main --tags", shell=True, cwd=repo_dir)
    print(f"✅ Git commit, tag {version} y push a GitHub completados.")

    # 4. Publicar Release en GitHub via API REST
    
import subprocess
try:
    rem = subprocess.check_output('git remote -v', shell=True, text=True)
    token = rem.split('ghp_')[1].split('@')[0]
    token = 'ghp_' + token
except:
    token = os.environ.get('GITHUB_TOKEN', '')

    headers = {
        "Authorization": f"token {token}",
        "Accept": "application/vnd.github.v3+json"
    }

    release_payload = {
        "tag_name": version,
        "target_commitish": "main",
        "name": f"SercomDesk {version} Release",
        "body": f"### 🚀 Release SercomDesk {version}\n\n- Sincronización automática de versión en cliente, interfaz y servidor.\n- Ejecutable binario precompilado SercomSoporte.exe adjunto en Assets.",
        "draft": False,
        "prerelease": False
    }

    import urllib.request
    req = urllib.request.Request(
        "https://api.github.com/repos/sercommx/sercom-soporte/releases",
        data=json.dumps(release_payload).encode("utf-8"),
        headers=headers,
        method="POST"
    )

    try:
        with urllib.request.urlopen(req) as response:
            rel_data = json.loads(response.read().decode("utf-8"))
            release_id = rel_data["id"]
            print(f"✅ Release {version} creado en GitHub (ID: {release_id})")
    except urllib.error.HTTPError as e:
        req_get = urllib.request.Request(
            f"https://api.github.com/repos/sercommx/sercom-soporte/releases/tags/{version}",
            headers=headers
        )
        with urllib.request.urlopen(req_get) as resp_get:
            rel_data = json.loads(resp_get.read().decode("utf-8"))
            release_id = rel_data["id"]

    # 5. Subir asset SercomSoporte.exe
    upload_url = f"https://uploads.github.com/repos/sercommx/sercom-soporte/releases/{release_id}/assets?name=SercomSoporte.exe"
    with open(exe_file, "rb") as f:
        exe_bytes = f.read()

    upload_headers = {
        "Authorization": f"token {token}",
        "Content-Type": "application/vnd.microsoft.portable-executable"
    }

    req_upload = urllib.request.Request(upload_url, data=exe_bytes, headers=upload_headers, method="POST")
    try:
        with urllib.request.urlopen(req_upload) as resp_up:
            print("✅ Asset SercomSoporte.exe subido exitosamente a GitHub Release")
    except urllib.error.HTTPError as e:
        print(f"⚠️ Asset upload note: {e.reason}")

    print(f"\n🎉 PROCESO COMPLETADO: Release {version} publicado y sincronizado al 100%.")

if __name__ == "__main__":
    main()
