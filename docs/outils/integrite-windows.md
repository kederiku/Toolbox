# Intégrité de Windows

La page **Dépannage guidé** indique **Mode standard** ou **Mode administrateur** selon les droits effectifs de Toolbox. Dans **Intégrité de Windows**, choisissez une vérification des fichiers protégés (SFC) ou du magasin de composants (DISM). Les actions de réparation sont également disponibles. Lisez leur description et confirmez la réparation souhaitée. En mode standard, Windows peut demander une autorisation pour exécuter l’action.

La console de Windows présente le résultat détaillé. À la fin, **Lire le journal** affiche les dernières lignes de CBS ou de DISM dans Toolbox. Ce journal local peut concerner plusieurs opérations. Le rapport partagé reprend uniquement le suivi de la commande. Une opération DISM peut prendre plusieurs minutes et utiliser Windows Update pour récupérer les composants nécessaires. Après une réparation, relancez la vérification et reproduisez le symptôme. [Référence SFC](https://learn.microsoft.com/fr-fr/windows-server/administration/windows-commands/sfc) · [Référence DISM](https://learn.microsoft.com/fr-fr/windows-hardware/manufacture/desktop/repair-a-windows-image?view=windows-11)

[Retour au catalogue](../../README.md)
