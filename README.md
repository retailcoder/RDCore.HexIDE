# RDCore.HexIDE
<sup>_Ce document est disponible en [français](./README.fr.md)_</sup>

![VIVAT CUCUMIS](./assets/vivat-cucumis-stonecore.png)

## Before we begin.
> 👋 Hi! New here? _Rubberduck_ was always an open-source initiative. **RDCore honors it with an Open-Core formula**.  
> <small>See [rubberduckvba.ca](https://rubberduckvba.ca) for more information.</small>

This repository holds **RDCore for HexIDE**, the add-in that brings the [RDCore](https://github.com/rubberduck-vba/RDCore) platform into the [HexIDE](https://github.com/hexide-io/HexIDE) IDE, under **⚖️GPLv3**:

- **RDCore.HexIDE**: the add-in. It registers RDCore's language server with HexIDE while an RDCore project (`.rdproj`) is open, opens `.rdproj` projects, and offers RDCore's debugger to F5. It talks to the platform through [RDCore.SDK.Protocol](https://github.com/rubberduck-vba/RDCore.SDK) over HexIDE's own language-server connection.
- **RDCore.PlatformInstaller**: gets the RDCore platform onto the machine. With your consent it downloads the platform from RDCore's GitHub releases, checks it and deploys it, and keeps it up to date. It has no dependency on HexIDE, and will become part of RDCore's own installer.

The platform itself, the RD-VBA runtime included, lives in the [RDCore](https://github.com/rubberduck-vba/RDCore) repository and runs as separate processes. What you build with RDCore is yours (see [RDCore's NOTICE](https://github.com/rubberduck-vba/RDCore/blob/main/NOTICE.md)).

## 📊 Project Status
RDCore is in active **pre-alpha** development, and this add-in with it. It builds on add-in seams HexIDE is still reviewing, so it runs on HexIDE builds that have them.

**Contributions**  

- Individuals: ✅ Open ([CLA](CLA.md))  
- Corporate: ⏳ Planned  

[![Build and Test](https://github.com/rubberduck-vba/RDCore.HexIDE/actions/workflows/build.yml/badge.svg)](https://github.com/rubberduck-vba/RDCore.HexIDE/actions/workflows/build.yml)

## Building

From the repository root, with the .NET 10 SDK and PowerShell 7:

```
pwsh tools/Get-Dependencies.ps1
dotnet build RDCore.HexIDE.slnx
dotnet test RDCore.HexIDE.slnx
```

`Get-Dependencies.ps1` fetches the sources the add-in builds against, at the versions `tools/dependencies.json` pins, into `deps/`: HexIDE's add-in contract (`HexIDE.Core`), and `RDCore.SDK.Protocol` until it is published as a package.

---
### See also
- [RDCore](https://github.com/rubberduck-vba/RDCore)
- [RDCore.SDK](https://github.com/rubberduck-vba/RDCore.SDK)
- [HexIDE](https://github.com/hexide-io/HexIDE)
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

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
