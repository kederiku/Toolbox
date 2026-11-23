# Workflow Git et GitHub Actions

Les changements arrivent sur `main` par une pull request (PR). La fusion rend le changement disponible dans les sources. Pour distribuer une nouvelle version de **Toolbox.exe**, le mainteneur publie ensuite un tag.

## Proposer un changement

Depuis une copie du dépôt, créez une branche à partir de la dernière version de `main`. Adaptez le nom de la branche au changement proposé. Cet exemple sert à modifier le README.

```powershell
git fetch origin
git switch -c docs/clarifier-aide origin/main
```

Après vos modifications, vérifiez le diff puis ajoutez les fichiers concernés. Pour un changement du logiciel, les [commandes de compilation et de test](developpement.md) permettent de reproduire le contrôle Windows en local.

```powershell
git diff
git add -- README.md
git diff --staged
git commit -m "Clarifier l’aide dans le README"
git push -u origin HEAD
```

Sur GitHub, ouvrez **Compare & pull request**. Choisissez `main` comme branche de destination et votre branche comme source. Dans la description, indiquez ce qui change et comment vous l’avez vérifié.

## Vérifier et fusionner la PR

Le workflow **Compiler et tester Toolbox** se lance à chaque push et à l’ouverture ou à la mise à jour d’une PR. Dans l’onglet **Checks** de la PR, le contrôle obligatoire **windows** compile `Toolbox.exe` et exécute les tests. En cas d’échec, ouvrez l’étape en rouge pour lire son journal. Poussez la correction sur la même branche pour relancer les contrôles. [Fichier du workflow](../.github/workflows/toolbox.yml)

La protection de `main` exige une PR à jour avec la branche cible et un contrôle **windows** réussi. Le nombre d’approbations exigé est fixé à zéro, ce qui permet au mainteneur de fusionner sa propre PR. GitHub bloque les envois forcés et la suppression de `main`.

Si `main` a avancé, cliquez sur **Update branch** dans la PR ou intégrez les changements depuis votre branche de travail avec les commandes ci-dessous. En cas de conflit, résolvez les fichiers concernés et terminez la fusion avant de pousser. Une fois les contrôles validés sur la révision à jour, utilisez le bouton de fusion de la PR.

```powershell
git fetch origin
git merge origin/main
git push
```

Pour essayer une compilation, ouvrez son exécution dans **Actions** puis téléchargez l’archive **Toolbox-Windows** dans **Artifacts**. Pour lancer une vérification à la demande, choisissez **Actions → Compiler et tester Toolbox → Run workflow**.

## Publier une version de Toolbox

1. Dans la PR de la version `X.Y.Z`, réglez `AssemblyVersion` dans [src/App.cs](../src/App.cs) et la version de `assemblyIdentity` dans [src/app.manifest](../src/app.manifest) sur `X.Y.Z.0`. Toolbox vérifie que la version de l’exécutable téléchargé correspond exactement au tag `vX.Y.Z`.
2. Après la fusion, copiez depuis GitHub le SHA du commit de fusion sur `main`. Dans PowerShell, saisissez ce SHA et le numéro de version sans le préfixe `v`, puis vérifiez le commit affiché.

   ```powershell
   git fetch origin main --tags
   $commit = Read-Host "SHA du commit fusionné sur main"
   $version = Read-Host "Version préparée dans la PR (X.Y.Z)"
   git show --no-patch --oneline $commit
   ```

3. Créez le nouveau tag sur ce commit. Si la création réussit, poussez ce tag sur GitHub.

   ```powershell
   git tag "v$version" $commit
   git push origin "refs/tags/v$version"
   ```

4. Dans **Actions**, ouvrez l’exécution associée au tag. Après le contrôle **windows**, la tâche **Publier Toolbox.exe** récupère la compilation testée et la joint à la version dans **Releases**. Vérifiez que **Toolbox.exe** y apparaît et que le lien permanent du README permet de le télécharger. [Versions publiées](https://github.com/kederiku/Toolbox/releases) · [Téléchargement de la dernière version](https://github.com/kederiku/Toolbox/releases/latest/download/Toolbox.exe)
