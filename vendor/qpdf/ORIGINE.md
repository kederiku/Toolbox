# Moteur PDF embarqué

Toolbox intègre qpdf 12.4.2 pour Windows x86. Le ZIP local contient `qpdf.exe`, `qpdf30.dll` et les trois DLL du runtime MinGW de la distribution officielle. L’empreinte SHA-256 du ZIP officiel `qpdf-12.4.2-mingw32.zip` est `6af53218eec293debb21e8036a95dfda0d690eef09f2f9023a22db0e3ad061fa`. [Publication officielle et sources](https://github.com/qpdf/qpdf/releases/tag/v12.4.2)

Les fichiers `LICENSE.txt` et `NOTICE.md` proviennent du paquet source qpdf 12.4.2. Le ZIP contient aussi les textes de licence des composants tiers. [GCC et exception du runtime](https://gcc.gnu.org/licensing/), [winpthreads](https://github.com/mingw-w64/mingw-w64/tree/master/mingw-w64-libraries/winpthreads), [libjpeg-turbo et IJG](https://github.com/libjpeg-turbo/libjpeg-turbo), [zlib](https://github.com/madler/zlib), [OpenSSL](https://github.com/openssl/openssl)

Pour actualiser le moteur, vérifier l’empreinte du ZIP officiel, recopier ses cinq fichiers binaires et les notices dans le ZIP embarqué, mettre à jour les références des deux compilations et exécuter les tests PDF.
