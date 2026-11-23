# Moteur de conversion embarqué

L’archive ZIP de ce dossier contient l’exécutable statique ImageMagick 7.1.2-36 Q8 pour Windows x86, `colors.xml`, `sRGB.icc` et les fichiers `LICENSE.txt` et `NOTICE.txt` de la distribution officielle. La version x86 peut aussi s’exécuter sur Windows x64. L’empreinte SHA-256 de l’archive 7z d’origine est `ea1f1867555159830277ba3e0a17924c2bf41234df8c78fa3d8fe98067df8a03`. [Distribution officielle](https://github.com/ImageMagick/ImageMagick/releases/tag/7.1.2-36)

La bibliothèque OpenMP `vcomp140.dll` version 14.52.37029.0 provient du redistribuable Microsoft Visual C++ v14 x86 signé par Microsoft. Toolbox l’extrait à côté du moteur pour la durée de son utilisation. [Distribution Microsoft](https://aka.ms/vc14/vc_redist.x86.exe) · [Conditions du composant](https://aka.ms/VCRedistLicense)

Pour mettre le moteur à jour, vérifier l’empreinte de l’archive officielle, reconstituer le ZIP avec ces fichiers à sa racine, adapter sa référence dans les deux modes de compilation puis tester les formats du catalogue. La configuration locale `src/ImagePolicy.xml` désactive les programmes de conversion externes et liste les décodeurs autorisés.
