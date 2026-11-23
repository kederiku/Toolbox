# Toolbox

**[Télécharger Toolbox.exe pour Windows](https://github.com/kederiku/Toolbox/releases/latest/download/Toolbox.exe)**

Toolbox réunit 14 outils en français pour Windows 10 et 11. Double-cliquez sur **Toolbox.exe** pour ouvrir le catalogue. L’application est portable et vous pouvez copier l’exécutable seul sur un autre PC Windows.

## Catalogue des outils

### Matériel

- [Diagnostic des périphériques](docs/outils/diagnostic-peripheriques.md) — Identifier un appareil en erreur et suivre les étapes de résolution.
- [Carte des branchements USB](docs/outils/carte-usb.md) — Localiser un branchement, repérer une coupure ou retrouver un port COM.

### Réseau

- [Test de débit Internet](docs/outils/test-debit.md) — Mesurer la réception, l’envoi et la latence, puis comparer les essais.
- [Dépannage réseau](docs/outils/depannage-reseau.md) — Trouver la cause d’un problème de connexion et appliquer la correction proposée.
- [Appareils du réseau](docs/outils/appareils-reseau.md) — Identifier les appareils du réseau local et compléter leurs noms depuis la box.

### Système

- [Performances du PC](docs/outils/performances-pc.md) — Suivre le processeur, la mémoire et le disque pendant un ralentissement.
- [Intégrité de Windows](docs/outils/integrite-windows.md) — Vérifier ou réparer les fichiers système et le magasin de composants avec SFC ou DISM.

### Fichiers

- [Convertir une image](docs/outils/convertir-image.md) — Changer le format d’une photo, d’une icône ou d’une animation.
- [Convertir une vidéo](docs/outils/convertir-video.md) — Choisir le format, l’encodage et la qualité d’une copie vidéo.
- [Convertir un fichier audio](docs/outils/convertir-audio.md) — Convertir un enregistrement ou extraire la piste sonore d’une vidéo.
- [Télécharger une vidéo YouTube](docs/outils/telecharger-video-youtube.md) — Enregistrer une vidéo en MP4 ou sa bande-son en MP3.
- [Ajouter un filigrane](docs/outils/ajouter-filigrane.md) — Marquer une copie d’un PDF, d’un document Word ou d’une image.
- [Nettoyeur de métadonnées](docs/outils/nettoyeur-metadonnees.md) — Retirer les données telles que le GPS ou l’auteur avant de partager une copie.
- [Rechercher les doublons](docs/outils/rechercher-doublons.md) — Comparer les fichiers identiques et les photos proches avant de choisir les copies à garder.

## Naviguer dans Toolbox

Utilisez **Ctrl+K** pour chercher par nom, usage ou format, par exemple **débit**, **FLAC** ou **EXIF**. Après avoir ouvert une carte, cliquez sur **Retour** ou appuyez sur **Alt+←** pour retrouver votre recherche, le filtre de famille et votre position dans la liste. Pour revoir tous les outils, choisissez **Catalogue** dans le menu.

Dans le menu latéral, dépliez une famille pour ouvrir directement un outil. En fenêtre étroite, cliquez sur **Menu** pour retrouver les mêmes destinations. Si vous partez d’un problème, ouvrez **Dépannage guidé** et choisissez le symptôme observé. Le rapport d’intervention et le guide sont également accessibles dans le menu.

Ouvrez **Paramètres** en bas du menu. Décochez **Menu latéral** pour masquer le raccourci d’un outil. Vous pouvez toujours ouvrir cet outil depuis le catalogue. Cochez **Favori** pour placer sa carte en tête de l’accueil, repérée par une étoile. Chaque choix est enregistré pour votre compte Windows.

## Mettre Toolbox à jour

À partir de la version 1.7.0, Toolbox consulte la dernière version stable sur GitHub au démarrage. Si une mise à jour est disponible, un bandeau affiche son numéro et la taille du fichier. Cliquez sur **Mettre à jour** pour le télécharger, ou sur **Plus tard** pour continuer. Hors connexion, vous pouvez utiliser les outils comme d’habitude.

Une fois le fichier vérifié, cliquez sur **Redémarrer et installer**. Toolbox se ferme, remplace l’exécutable à son emplacement actuel et rouvre la nouvelle version. Vos préférences et votre historique sont conservés. Si le dossier est protégé, Toolbox propose de télécharger la version sur GitHub.

Si vous utilisez encore Toolbox 1.6.0, fermez-le et remplacez une dernière fois son exécutable par celui du lien en haut de cette page.

## Rapport d’intervention

Ouvrez **Rapport d’intervention** depuis le menu. Les contrôles effectués pendant la session y apparaissent avec leur heure, leur résultat et les mesures disponibles. Les événements USB y figurent sous des descriptions partageables. **Copier le rapport** place le texte dans le presse-papiers. Copiez-le avant de fermer l’application si vous souhaitez conserver une trace.

## Développement

- [Guide de développement](docs/developpement.md) — Compiler Toolbox, lancer les tests et ajouter un outil.
- [Workflow Git et GitHub Actions](docs/workflow-git.md) — Proposer un changement, le fusionner sur `main` et publier une version téléchargeable.
