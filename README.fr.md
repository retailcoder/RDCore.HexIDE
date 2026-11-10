# RDCore.HexIDE
<sup>_This document is available in [English](./README.md)_</sup>

![VIVAT CUCUMIS](./assets/vivat-cucumis-stonecore.png)

## Avant de commencer.
> 👋 Bonjour! Nouveau ici? _Rubberduck_ a toujours été une initiative open-source. **RDCore l'honore avec une formule Open-Core**.  
> <small>Voir [rubberduckvba.ca](https://rubberduckvba.ca) pour plus d'informations.</small>

Ce dépôt contient **RDCore pour HexIDE**, l'add-in qui amène la plateforme [RDCore](https://github.com/rubberduck-vba/RDCore) dans l'IDE [HexIDE](https://github.com/hexide-io/HexIDE), sous **⚖️GPLv3 ou toute version ultérieure** :

- **RDCore.HexIDE** : l'add-in. Il inscrit le serveur de langage de RDCore auprès de HexIDE tant qu'un projet RDCore (`.rdproj`) est ouvert, ouvre les projets `.rdproj`, et offre le débogueur de RDCore à F5. Il parle à la plateforme par RDCore.SDK.Protocol, un assembly du paquet [RDCore.SDK](https://www.nuget.org/packages/RDCore.SDK), sur la connexion de serveur de langage de HexIDE elle-même.
- **RDCore.PlatformInstaller** : installe la plateforme RDCore sur le poste. Avec votre consentement, il télécharge la plateforme depuis les publications GitHub de RDCore, la vérifie, la déploie et la tient à jour. Il ne dépend aucunement de HexIDE et fera partie de l'installateur de RDCore lui-même.

La plateforme elle-même, incluant le runtime RD-VBA, se trouve dans le dépôt [RDCore](https://github.com/rubberduck-vba/RDCore) et s'exécute dans des processus distincts. Ce que vous construisez avec RDCore vous appartient (voir l'[AVIS de RDCore](https://github.com/rubberduck-vba/RDCore/blob/main/NOTICE.md)).

## 📊 État du projet
RDCore est en développement **pré-alpha** actif, et cet add-in avec lui. Il s'appuie sur des points d'extension que HexIDE révise encore : il fonctionne donc avec les versions de HexIDE qui les ont.

**Contributions**  

- Individus : ✅ Ouvertes ([CLA](CLA.fr.md))  
- Corporatives : ⏳ Prévues  

[![Build and Test](https://github.com/rubberduck-vba/RDCore.HexIDE/actions/workflows/build.yml/badge.svg)](https://github.com/rubberduck-vba/RDCore.HexIDE/actions/workflows/build.yml)

## Bâtir

À partir de la racine du dépôt, avec le SDK .NET 10 et PowerShell 7 :

```
pwsh tools/Get-Dependencies.ps1
dotnet build RDCore.HexIDE.slnx
dotnet test RDCore.HexIDE.slnx
```

`Get-Dependencies.ps1` récupère dans `deps/` les sources contre lesquelles l'add-in est bâti, aux versions que fixe `tools/dependencies.json` : le contrat d'add-in de HexIDE (`HexIDE.Core`). Le paquet RDCore.SDK provient de nuget.org.

`pwsh tools/Pack-Addin.ps1` dispose l'add-in en paquet d'add-in HexIDE dans `artifacts/addins/rdcore/` : copiez ce dossier dans le dossier `addins` à côté de l'exécutable de HexIDE. Le paquet n'est pas encore signé : HexIDE ne le charge donc qu'en mode développeur, avec les add-ins non signés permis.

### Réglages de développement

Deux réglages servent au travail sur RDCore et sur l'add-in. L'add-in les lit dans `%LOCALAPPDATA%\RDCore\HexIDE\settings.json`, dans toutes ses versions, Release comprise : le fichier est dans le profil de l'utilisateur, et qui ne l'écrit jamais n'est touché en rien.

```json
{
  "platformRoot": "C:\\Dev\\RDCore\\artifacts\\rdcore-dev",
  "developmentFeed": "C:\\Dev\\RDCore.HexIDE\\artifacts\\feed"
}
```

- `platformRoot` exécute la plateforme de ce dossier, comme le `artifacts/rdcore-dev` d'un build, au lieu d'une plateforme installée. Rien n'est téléchargé tant qu'il est réglé, et il l'emporte sur `developmentFeed`.
- `developmentFeed` télécharge les publications d'un dossier plutôt que de GitHub, pour essayer la question, le téléchargement et l'installation avant qu'une publication n'existe. `pwsh tools/New-DevelopmentFeed.ps1 -Platform <une plateforme bâtie> -Version 0.1.0-dev.1 -Destination <dossier>` en écrit un. Le téléchargement est vérifié comme celui d'une publication : les hôtes, https, et sa somme de contrôle.

## Licence
RDCore.HexIDE est licencié sous la Licence publique générale GNU, version 3 ou toute version ultérieure : voir [LICENSE-GPLv3.md](LICENSE-GPLv3.md). Le fichier de licence reproduit le texte officiel, en anglais. Une version française de la licence est disponible sur demande.

---
### Voir aussi
- [RDCore](https://github.com/rubberduck-vba/RDCore)
- [RDCore.SDK](https://github.com/rubberduck-vba/RDCore.SDK)
- [HexIDE](https://github.com/hexide-io/HexIDE)
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [CODE_OF_CONDUCT.fr.md](CODE_OF_CONDUCT.fr.md)

<hr/>
<p align='left' style='margin-left: 32px;'>
<a href='https://rubberduck-vba.github.io/RDCore/index.fr.html'>ACCUEIL</a> • <a href='https://rubberduck-vba.github.io/RDCore/index.html'>HOME</a>  | ℹ️ <a href='https://rubberduck-vba.github.io/RDCore/introduction.fr.html'>BIENVENUE</a> • <a href='https://rubberduck-vba.github.io/RDCore/introduction.html'>WELCOME</a>  | 🧩 <a href='https://rubberduck-vba.github.io/RDCore/getting-started.fr.html'>BÂTISSONS</a> • <a href='https://rubberduck-vba.github.io/RDCore/getting-started.html'>BUILD</a>  | <a href='https://rubberduck-vba.github.io/RDCore/specs/rd-vbal.html'><strong>RD-VBAL</strong></a>  |  <a href='https://rubberduck-vba.github.io/RDCore/api/RDCore.SDK.Model.Errors.VBCompileErrorId.html'>SDK</a>  | 🌐 <a href='https://rubberduckvba.ca'>rubberduckvba.ca</a>
</p>
<hr/>
<p align='center'><img alt='Logo™ 9562-7303 Québec inc.' src='./assets/vector-ducky.svg' style='width:200px; align:center;' /></p>
<h6 align='center'>V I V A T ❤️ C U C U M I S ™</h6>
<p align='center' style='font-size:8pt;'>
<small>© Copyright <strong>9562-7303 Québec inc.</strong> (2026)<br/><em>Seul, &quot;Rubberduck&quot; est utilisé pour fins de référence au projet open-source legacy <strong>utilisé publiquement ainsi depuis 2015</strong> et sans lien ni affiliation avec tout tiers détenteur d'une marque semblable dans quelque juridiction que ce soit.<br/>&quot;Rubberduck VBA&quot;, &quot;RDCore&quot; et &quot;VIVAT CUCUMIS&quot; sont des marques de commerce revendiquées par 9562-7303 Québec inc. (en attente); Toutes les marques appartiennent à leur détenteur respectif.<br/>RDCore n'est pas un produit de Microsoft et n'est pas affilié à Microsoft, ni directement, ni indirectement.<br/><br/>If used alone, <em>&quot;Rubberduck&quot; is used as a reference to the legacy open-source project <strong>the same way it has been used publicly since 2015</strong> and without any links or affiliation with any third-party trademark holders of a similar trademark in any jurdisdiction.<br/>&quot;Rubberduck VBA&quot;, &quot;RDCore&quot; and &quot;VIVAT CUCUMIS&quot; are trademarks claimed by 9562-7303 Québec inc. (pending). All trademarks belong to their respective owners.<br/>RDCore is not a Microsoft product and is not affiliated with Microsoft, directly or indirectly.</small>
</p>
