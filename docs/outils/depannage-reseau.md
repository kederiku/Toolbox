# Dépannage réseau

Ouvrez **Réseau → Dépannage réseau** puis **Lancer le diagnostic**. Toolbox examine les cartes actives et, si le PC est connecté à un réseau, demande une courte réponse au serveur de test Microsoft. Le résultat indique la prochaine action, par exemple reconnecter le Wi-Fi, valider un portail de connexion ou vérifier la box. Un réseau d’entreprise peut filtrer ce serveur. [Test de connectivité de Microsoft](https://learn.microsoft.com/fr-fr/troubleshoot/windows-server/networking/troubleshoot-ncsi-guidance)

Quand les noms de test échouent à la résolution, **Vider le cache DNS** permet à Windows de redemander leurs adresses. Lorsqu’une carte en configuration automatique attend une adresse IP, **Renouveler l’adresse IP** cible la carte indiquée. Cliquez sur la correction souhaitée puis acceptez l’autorisation Windows si elle est demandée. Toolbox affiche le résultat de la commande et vérifie de nouveau la connexion. [Commandes réseau de Windows](https://learn.microsoft.com/fr-fr/windows-server/administration/windows-commands/ipconfig)

Les détails du contrôle indiquent ce que l’outil a pu vérifier sur la liaison, les adresses, la passerelle, la résolution des noms et la réponse du serveur. Après une correction, retrouvez les observations avant et après l’opération dans le rapport.

[Retour au catalogue](../../README.md)
