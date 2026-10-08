# Atlas — gestionnaire de projets local

Atlas est une application de gestion de projets de type Kanban qui fonctionne **en local sur votre PC** : tableaux, listes, cartes, étiquettes, listes de tâches, commentaires avec pièces jointes, échéances et cartes récurrentes. Vos données restent sur votre machine.

## Fonctionnalités

- Tableaux regroupables en groupes et sous-groupes (renommage direct depuis la barre latérale).
- Cartes avec description, commentaires, pièces jointes, étiquettes, liste de tâches, échéance.
- **Cartes récurrentes** : choisissez un jour de la semaine et une heure (`HH:MM`). Quand la carte est terminée, elle est archivée et une nouvelle occurrence est créée.
- **Archivage automatique** hebdomadaire des cartes terminées, planifiable depuis le menu de la liste `ARCHIVE`.
- Sauvegarde automatique, récupération d'une sauvegarde, export et import des données.
- Thèmes, fonds de tableau personnalisés, groupes masquables par mot de passe.

## Installation (Windows 10/11, sans droits administrateur)

Quelle que soit la méthode, Atlas est installé dans `%LOCALAPPDATA%\Atlas` et un raccourci **Atlas** est créé sur le Bureau.

### Méthode 1 — Installateur graphique (recommandée)

Téléchargez [`Atlas-Setup.exe`](../../releases/latest/download/Atlas-Setup.exe) depuis la page [Releases](../../releases/latest) et lancez-le.

### Méthode 2 — Une ligne PowerShell

```powershell
irm https://raw.githubusercontent.com/rasheequa/Atlas-gestionnaire-de-projet/main/install.ps1 | iex
```

Le script télécharge la dernière version et lance l'installateur. Options (après téléchargement du script) : `-Silent` (sans fenêtre) et `-InstallDir <dossier>`.

### Méthode 3 — curl / wget (téléchargement direct)

```powershell
curl.exe -L -o Atlas-Setup.exe https://github.com/rasheequa/Atlas-gestionnaire-de-projet/releases/latest/download/Atlas-Setup.exe
.\Atlas-Setup.exe /VERYSILENT
```

Sous Linux/macOS ou Git Bash, `wget -O Atlas-Setup.exe <même URL>` fonctionne aussi pour récupérer le fichier (Atlas lui-même ne tourne que sous Windows).

### Méthode 4 — Script Python

```powershell
python install.py            # ou : python install.py --silent --dir C:\Atlas
```

> Windows SmartScreen peut afficher un avertissement car l'installateur n'est pas signé : choisissez **Informations complémentaires → Exécuter quand même**.
> Derrière un proxy d'entreprise, utilisez de préférence les méthodes 1 ou 2 (elles réutilisent vos identifiants Windows).

## Utilisation

- Double-cliquez sur le raccourci **Atlas** : une fenêtre Microsoft Edge dédiée (profil séparé, sans impact sur votre navigateur) s'ouvre sur l'application.
- La fermeture de la fenêtre arrête automatiquement le serveur local.
- Atlas utilise l'adresse locale `http://127.0.0.1:51735` (accessible uniquement depuis votre PC).

## Où sont mes données ?

| Élément | Emplacement |
| --- | --- |
| Application | `%LOCALAPPDATA%\Atlas` |
| Sauvegarde des tableaux | `Documents\Atlas\atlas-sync.json` |
| Exports | `Documents\Atlas\exported` |

Les données ne sont **jamais** envoyées sur Internet et ne sont pas incluses dans l'installateur.

## Espace cloud (Google Drive, Dropbox, OneDrive)

Dans **Sécurité & synchronisation → Espace cloud**, connectez votre compte pour enregistrer `atlas-sync.json` dans un **dossier privé de l'application** (Google Drive : dossier caché de l'appli ; Dropbox : `Applications/Atlas` ; OneDrive : `Applications/Atlas`), puis le récupérer depuis un autre PC. Atlas n'accède à aucun autre fichier de votre cloud.

- Authentification officielle **OAuth 2.0 avec PKCE** dans votre navigateur : Atlas ne voit jamais votre mot de passe.
- Les jetons d'accès sont chiffrés par Windows (DPAPI, compte courant) dans `%LOCALAPPDATA%\Atlas\cloud-tokens.dat`.
- Cette fonction n'est disponible que dans l'application installée (pas dans `Atlas.html` ouvert seul).

### Configuration (administrateur de l'application)

Chaque fournisseur demande une **application OAuth** (gratuite). Créez-les une fois, puis renseignez leurs identifiants publics dans `CloudService.cs` (constantes `Default…`) ou dans `Documents\Atlas\cloud-config.json` :

```json
{
  "google":    { "clientId": "…", "clientSecret": "…" },
  "dropbox":   { "clientId": "…" },
  "microsoft": { "clientId": "…" }
}
```

| Fournisseur | À créer | Redirection autorisée | Autorisation |
| --- | --- | --- | --- |
| Google | Google Cloud Console → Identifiants → ID client OAuth, type **Application de bureau** | `http://127.0.0.1:51735/__cloud/callback` | `drive.appdata` |
| Dropbox | App Console → **Scoped access**, **App folder** | `http://127.0.0.1:51735/__cloud/callback` | `files.content.read`, `files.content.write` |
| Microsoft | Entra ID → Inscription d'application, comptes Microsoft personnels, plateforme **Mobile et bureau** | `http://localhost:51735/__cloud/callback` | `Files.ReadWrite.AppFolder` |

Tant qu'un fournisseur n'est pas configuré, Atlas affiche « Non configuré » à côté de son nom.

## Mises à jour

À chaque lancement, Atlas vérifie si une nouvelle version de `Atlas.html` est disponible sur la branche `main` de ce dépôt et la télécharge automatiquement. Aucune action n'est nécessaire : relancez simplement Atlas pour profiter des nouveautés. Hors connexion, la dernière version téléchargée est utilisée.

Les changements du lanceur (`Atlas.exe`) nécessitent en revanche de réinstaller la dernière version de `Atlas-Setup.exe` : vos données sont conservées.

## Désinstallation

Paramètres Windows → Applications → **Atlas** → Désinstaller. Le dossier `Documents\Atlas` (vos données) n'est pas supprimé.

## Développement

Prérequis : .NET 8 SDK et [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet publish Atlas.App.csproj -c Release -r win-x64 --self-contained true -o publish `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DRoot=. AtlasSetup.iss
```

Pour publier une nouvelle version de l'installateur, créez et poussez un tag :

```powershell
git tag v1.0.1
git push origin v1.0.1
```

Le workflow GitHub Actions `Release Atlas` construit alors `Atlas-Setup.exe` et l'ajoute aux Releases.
