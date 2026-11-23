# SDK Microsoft Edge WebView2

Les deux bibliothèques .NET et les trois chargeurs natifs proviennent du paquet **Microsoft.Web.WebView2 1.0.2478.35** publié par Microsoft sur NuGet. Ses bibliothèques `net45` conviennent au projet .NET Framework 4.6. Le moteur de navigation est le Runtime WebView2 installé sur le PC. [Paquet Microsoft sur NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2478.35)

L’empreinte SHA-256 du fichier `.nupkg` est `b1df2d7fe955a3378603735f910b126647e43033213b5cc071b8d6f0168a3b96`. Les cinq DLL ont une signature Authenticode Microsoft Corporation vérifiée.

| Dans le paquet | Dans ce dossier |
| --- | --- |
| `lib/net45/Microsoft.Web.WebView2.Core.dll` et `lib/net45/Microsoft.Web.WebView2.Wpf.dll` | `lib/` |
| `runtimes/win-{x86,x64,arm64}/native/WebView2Loader.dll` | `{x86,x64,arm64}/WebView2Loader.dll` |
| `LICENSE.txt` et `NOTICE.txt` | À la racine |

Les deux modes de compilation embarquent ces fichiers, y compris les notices, dans l’exécutable portable.
