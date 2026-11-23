# Développer Toolbox

## Compiler et vérifier

Le projet utilise C#, WPF et .NET Framework 4.6 ou ultérieur. Le script utilise le compilateur du .NET Framework de Windows. Depuis le dossier du projet, dans PowerShell, exécutez la première commande pour produire l’exécutable et la seconde pour lancer les contrôles.

~~~powershell
.\compiler.ps1
.\compiler.ps1 -Tester
~~~

Les contrôles couvrent les explications, les critères de réactivation, la vérification du résultat, l’inventaire réel en lecture seule et le parcours graphique sur des données de test. Ils vérifient également les conversions d’images, les animations, la fermeture pendant une lecture et la conservation des fichiers en cas d’échec. Pour travailler dans Visual Studio avec les références .NET Framework 4.6, ouvrez le fichier Toolbox.csproj.

Les deux méthodes de compilation incorporent les moteurs et leurs notices dans l’exécutable. Les contrôles audio et vidéo encodent de courts extraits, relisent les fichiers produits et vérifient la protection des originaux. Les contrôles audio vérifient aussi le choix de la piste et comparent les échantillons de plusieurs formats sans perte. [Provenance d’ImageMagick](../vendor/ImageMagick/ORIGINE.md) · [Provenance de FFmpeg](../vendor/FFmpeg/ORIGINE.md)

## Ajouter un outil

1. Ajoutez une vue nommée dans `src/Fenetre.xaml`, à côté de `SpeedPage`, et placez sa logique dans un fichier C# du dossier `src`.
2. Dans `CreateToolCatalog()` de `src/ToolCatalog.cs`, déclarez une `NavigationPage` sur le modèle du test de débit et incluez-la dans la liste retournée. Donnez-lui un nom de bouton unique, une vue, une icône, ses libellés et une catégorie. Associez l’outil à la famille appropriée dans le catalogue et vérifiez son accès depuis la recherche et le menu latéral.
3. Si l’outil doit charger des données à l’ouverture ou arrêter un travail en cours à la sortie, renseignez `OnOpen` ou `OnLeave`. Le bouton principal de la vue peut être indiqué dans `PrimaryAction` pour recevoir le focus depuis le catalogue.
4. Créez la fiche de l’outil dans `docs/outils/` et ajoutez son lien dans le [catalogue du README](../README.md).

## Publier une nouvelle version sur GitHub

Le workflow [Compiler et tester Toolbox](../.github/workflows/toolbox.yml) compile le logiciel sous Windows et lance les tests à chaque push et pour les pull requests. Vous pouvez aussi le lancer depuis l’onglet **Actions** avec **Run workflow**. Conservez le dossier `vendor/` dans le dépôt Git pour que la compilation retrouve les moteurs embarqués.

Pour publier une version, créez un tag au format `vX.Y.Z` sur le commit à distribuer et poussez ce tag sur GitHub. Une fois les tests réussis, le workflow joint **Toolbox.exe** à la version dans **Releases**. Le lien du [README](../README.md) télécharge directement l’exécutable de la dernière version.

[Retour au README](../README.md)
