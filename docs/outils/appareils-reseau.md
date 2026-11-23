# Appareils du réseau

Ouvrez **Réseau → Appareils du réseau**, choisissez votre connexion Wi-Fi ou Ethernet, puis cliquez sur **Rechercher les appareils**. Chaque ligne indique une adresse IPv4 et l’adresse MAC obtenue sur cette connexion lorsqu’elle est disponible. **(ce PC)** désigne votre ordinateur. Si la passerelle configurée répond sans fournir de nom, elle apparaît sous **Passerelle du réseau**.

Cliquez sur un en-tête pour trier la colonne, puis cliquez à nouveau pour inverser l’ordre. Le filtre accepte une partie du nom, de l’IP ou de la MAC, par exemple `192.168.1.` ou `3C:AA:BB`.

**Nom inconnu** signifie que l’adresse a répondu, mais que Windows et les annonces UPnP locales n’ont fourni aucun nom. Sélectionnez une ligne pour ouvrir sa fiche. Un téléviseur ou une box compatible UPnP peut y indiquer son nom usuel, son fabricant et son modèle. [Découverte des voisins sous Windows](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-resolveipnetentry2) · [Découverte UPnP](https://openconnectivity.org/developer/specifications/upnp-resources/upnp/)

Sous **Ports TCP**, la fiche de ce PC présente ses ports en écoute sur la connexion choisie. Pour un autre appareil, la fiche indique le port ayant répondu à la lecture de sa description UPnP et les ports distants des connexions TCP actuellement établies avec ce PC, lorsqu’ils sont disponibles. Un port distant peut aussi être celui d’un client. Pour vérifier un numéro précis, saisissez-le et cliquez sur **Tester ce port**. Le résultat indique si une connexion TCP aboutit depuis ce PC au moment du test. [Connexions TCP observées par Windows](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.ipglobalproperties.getactivetcpconnections)

Le relevé couvre le sous-réseau IPv4 visible depuis la carte choisie. Un appareil en veille, sur le Wi-Fi invité, sur un autre sous-réseau ou uniquement en IPv6 peut manquer. Pour comparer, ouvrez la liste des **clients actuellement connectés** dans l’interface de la box. Vous pouvez annuler la recherche et consulter les réponses déjà reçues, ou la relancer pour mettre la liste à jour.

## Retrouver les noms des appareils auprès de la box

1. Dans **Réseau → Appareils du réseau**, sélectionnez votre connexion, puis **Lire depuis la box… → Ouvrir l’interface de la box…**.
2. Connectez-vous dans l’interface habituelle de la box. Affichez la rubrique **Appareils connectés**, **Clients** ou son équivalent, puis cliquez sur **Lire la liste affichée**. Si la liste comporte plusieurs pages, utilisez **Ajouter cette page** avant de passer à la suivante, puis **Terminer la lecture**.
3. Vérifiez les noms, les adresses et les états dans l’aperçu, puis cliquez sur **Importer la liste**. L’onglet **Depuis la box** présente les lignes retenues. Une ligne explicitement hors ligne est écartée. **Non précisé** signifie que la liste n’indiquait aucun état reconnu.
4. Dans **Depuis ce PC**, un appareil auparavant inconnu récupère le nom associé à sa MAC dans le relevé de la box. Sa fiche en indique la provenance. Vous pouvez lire la box avant ou après la recherche du PC. Une nouvelle importation remplace la précédente pour la connexion sélectionnée.

La lecture utilise les tableaux et les fiches affichés par l’interface locale. La compatibilité dépend de ce que le modèle et son logiciel rendent visible. Si l’interface utilise une autre adresse locale, saisissez-la dans la fenêtre de lecture. Si la gestion se fait depuis une application mobile ou si la page est illisible, utilisez **Utiliser une liste copiée**, collez les clients avec leurs en-têtes, puis cliquez sur **Afficher l’aperçu**. Cette option fonctionne aussi sur un PC dépourvu du navigateur intégré WebView2. [Runtime Microsoft WebView2](https://learn.microsoft.com/fr-fr/microsoft-edge/webview2/concepts/distribution)

La correspondance utilise une MAC complète et un nom sans ambiguïté. Vérifiez que le téléphone présente la même adresse Wi-Fi privée dans les deux relevés. Un équipement nommé dans la box mais sans MAC peut figurer dans son onglet avec son IP. Les listes importées sont conservées en mémoire pour la connexion sélectionnée.

[Retour au catalogue](../../README.md)
