# Carte des branchements USB

Ouvrez **Carte USB** dans le menu. Dépliez le contrôleur, le hub racine du PC puis les hubs raccordés en cascade. Chaque appareil apparaît sous son hub, avec le numéro du port lorsque Windows le fournit. Cliquez sur un appareil pour lire son chemin complet et son état. Cliquez sur **Actualiser la carte** après avoir déplacé un câble. Depuis une fiche de diagnostic, **Voir sur la carte USB** sélectionne l’appareil s’il est encore présent.

La carte représente les branchements détectés, y compris ceux des composants USB internes. La numérotation est locale à chaque hub. La correspondance avec une prise en façade ou à l’arrière dépend du PC. Une branche dont Windows ne fournit pas le parent apparaît sous **Rattachement indéterminé**.

Sur un appareil multifonction, la fiche signale aussi les fonctions que Windows indique en erreur. Par exemple, une webcam peut être reconnue alors que sa fonction vidéo manque de pilote. Sélectionnez-la pour lire le code Windows et les étapes de résolution de la fonction concernée. [Interfaces des périphériques USB composites](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/standard-usb-identifiers)

## Repérer une déconnexion intermittente

Ouvrez **Carte USB** une première fois, puis utilisez normalement l’accessoire. Le suivi continue pendant que vous consultez les autres pages de Toolbox. Dépliez **Activité des branchements** pour voir les apparitions, les disparitions et les retours horodatés. Si une même entrée disparaît plusieurs fois, essayez de changer un seul élément à la fois, par exemple le câble, puis comparez les observations.

**Suspendre le suivi** arrête les relevés automatiques. **Reprendre le suivi** établit un nouvel état de départ. Toutes les observations de la session sont consultables sur la carte et le rapport conserve la chronologie de la session sous des libellés partageables. La fermeture de l’application arrête le suivi. Windows signale un changement de périphériques et Toolbox compare les inventaires successifs. Une coupure suivie d’un retour entre deux relevés peut passer inaperçue. [Notifications de changement de périphériques Windows](https://learn.microsoft.com/en-us/windows/win32/devio/dbt-devnodes-changed)

## Suivre un appareil déplacé et son port COM

Ouvrez **Carte USB** avant de déplacer l’appareil. La carte se met à jour lors des notifications de branchement de Windows. **Actualiser la carte** permet aussi de déclencher la comparaison. Sous l’arbre, **Suivi des changements de port** présente l’ancien et le nouveau branchement. Toolbox indique « Même appareil reconnu » si Windows signale un identifiant unique et que l’identifiant d’instance correspond. Pour un adaptateur série, comparez les ports COM indiqués et choisissez le port actuel dans votre logiciel.

La fiche de l’adaptateur affiche également ses ports COM actifs dès le premier relevé, y compris ceux des interfaces enfants que Windows permet de lui rattacher. Pour un adaptateur à plusieurs interfaces, comparez les affectations proposées avec celle attendue par votre logiciel. Le rapport reprend les ports COM actuels dès le premier relevé et conserve les changements suivants.

Les relevés de la session restent en mémoire jusqu’à la fermeture de Toolbox. L’outil consulte aussi les anciennes installations encore enregistrées par Windows. Un autre exemplaire du même modèle peut avoir été utilisé auparavant. Vérifiez l’emplacement et les réglages avant de conclure à un déplacement.

Si une ancienne entrée déconnectée vous gêne, ouvrez **Examiner l’ancienne installation** lorsqu’il est proposé. Confirmez l’entrée concernée avant de demander son retrait. Toolbox vérifie de nouveau les deux installations après l’autorisation administrateur. Windows retire l’ancienne instance et ses fonctions associées et conserve le paquet de pilotes. Plusieurs correspondances possibles, un hub ou une entrée active excluent ce retrait. Le nettoyage ne réattribue pas automatiquement un numéro COM.

[Retour au catalogue](../../README.md)
