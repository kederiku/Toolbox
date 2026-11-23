# Moteur vidéo embarqué

Toolbox intègre les exécutables statiques Windows x64 de la distribution **FFmpeg 9.1 essentials** de Gyan Doshi. Le ZIP du projet contient `ffmpeg.exe`, `ffprobe.exe`, la licence GPLv3 et la notice `README.txt` originales. Les deux programmes proviennent du même paquet. [Distribution et bibliothèques incluses](https://www.gyan.dev/ffmpeg/builds/) · [Sources de la version distribuée](https://github.com/FFmpeg/FFmpeg/commit/74e4393595)

L’empreinte SHA-256 vérifiée du ZIP de distribution est `b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd`. Pour reproduire le ZIP embarqué, prendre `bin/ffmpeg.exe`, `bin/ffprobe.exe`, `LICENSE` et `README.txt` dans cette distribution et placer ces quatre fichiers à la racine d’une archive ZIP. [Archive de la version 9.1](https://github.com/GyanD/codexffmpeg/releases/tag/9.1)

Pour mettre à jour le moteur, vérifier l’empreinte fournie par le distributeur, actualiser les deux références de compilation et exécuter les tests de conversion. Le moteur utilise les encodeurs logiciels du paquet et fonctionne hors ligne.
