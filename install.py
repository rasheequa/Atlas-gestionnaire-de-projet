"""Télécharge et installe Atlas (Windows 10/11, sans droits administrateur).

Usage : python install.py [--silent] [--dir CHEMIN]
"""
import argparse
import os
import subprocess
import sys
import tempfile
import urllib.request

URL = "https://github.com/rasheequa/Atlas-gestionnaire-de-projet/releases/latest/download/Atlas-Setup.exe"


def main() -> int:
    if os.name != "nt":
        print("Atlas ne fonctionne que sous Windows.")
        return 1

    parser = argparse.ArgumentParser(description="Installe Atlas")
    parser.add_argument("--silent", action="store_true", help="installation sans fenêtre")
    parser.add_argument("--dir", default="", help="dossier d'installation")
    options = parser.parse_args()

    target = os.path.join(tempfile.gettempdir(), "Atlas-Setup.exe")
    print("Téléchargement d'Atlas...")
    urllib.request.urlretrieve(URL, target)

    command = [target]
    if options.silent:
        command += ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"]
    if options.dir:
        command.append(f"/DIR={options.dir}")

    print("Installation...")
    code = subprocess.call(command)
    os.remove(target)
    if code != 0:
        print(f"L'installation a échoué (code {code}).")
        return code
    print("Atlas est installé. Utilisez le raccourci Atlas sur le Bureau.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
