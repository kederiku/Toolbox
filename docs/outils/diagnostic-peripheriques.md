# Diagnostic des périphériques

1. Pour un problème matériel, ouvrez **Diagnostic des périphériques** dans le catalogue ou choisissez **Dépannage guidé → Un appareil ne fonctionne plus**. Branchez et allumez l’appareil à vérifier puis cliquez sur **Lancer l’analyse**.
2. Si Windows signale un problème, lisez l’explication sous le nom de l’appareil. Dépliez **Étapes de résolution** pour suivre la procédure.
3. Pour un appareil désactivé que vous souhaitez utiliser, choisissez **Réactiver cet appareil** et confirmez. Windows peut demander l’autorisation d’un administrateur. Toolbox vérifie ensuite l’état du même appareil.
4. Pour partager le résultat d’un essai, cliquez sur **Copier le résumé**, puis collez le texte dans votre message. Le résumé indique les versions de Toolbox et de Windows, la date de l’analyse, les problèmes détectés et la dernière demande de réactivation de la session. Les appareils concernés y figurent sous leur modèle ou sous un intitulé générique.

L’inventaire et l’analyse restent sur le PC. Les résultats reflètent les signalements de Windows au moment de la vérification. Un appareil totalement invisible pour Windows, un faux contact ou une panne intermittente peuvent nécessiter un essai de branchement. Une aide est disponible sous les résultats.

La réactivation intégrée utilise PnPUtil, disponible pour cette action à partir de Windows 10 version 2004 et sur Windows 11. Sur les versions antérieures, le guide indique comment réactiver l’appareil dans le Gestionnaire de périphériques. [Documentation Microsoft sur PnPUtil](https://learn.microsoft.com/fr-fr/windows-hardware/drivers/devtest/pnputil-command-syntax)

## Retrouver un périphérique USB inconnu

Si Windows affiche le code 43 avec une erreur de réinitialisation du port ou de lecture de la description USB, ouvrez **Retrouver cet appareil** sur sa fiche. Laissez le branchement en place jusqu’à ce que l’outil demande de retirer un accessoire. Cliquez sur **Vérifier le débranchement**, puis suivez les indications pour essayer la même prise avec le même accessoire. Après chaque vérification, **Copier le résumé** inclut l’observation du test.

La fiche affiche le repère du port attribué par Windows lorsqu’il est disponible. Les noms retrouvés dans les anciens branchements au même emplacement sont des pistes à confirmer. L’assistant relit l’inventaire après chaque manipulation. Si l’alerte revient, il propose des essais séparés de la prise et du câble. Un échec de communication USB peut survenir avant que l’appareil transmette son modèle. [Exemple de diagnostic USB de Microsoft](https://learn.microsoft.com/windows-hardware/drivers/usbcon/case-study--troubleshooting-an-unknown-usb-device-by-using-etw-and-netmon)

Les signalements sont lus via SetupAPI et le gestionnaire de configuration de Windows. Les explications correspondent aux codes du Gestionnaire de périphériques. [Référence Microsoft des codes](https://learn.microsoft.com/windows-hardware/drivers/install/device-manager-error-messages)

Pour situer l’appareil sur un hub ou suivre ses déconnexions, consultez la [carte des branchements USB](carte-usb.md).

[Retour au catalogue](../../README.md)
