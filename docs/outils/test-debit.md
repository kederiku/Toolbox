# Test de débit Internet

Dans le menu latéral, ouvrez **Réseau → Test de débit**, puis cliquez sur **Lancer le test**. La réception et l’envoi s’affichent en Mbit/s et en Mo/s (80 Mbit/s correspondent à 10 Mo/s). La latence HTTPS est la médiane de cinq allers-retours vers Cloudflare. La gigue mesure la variation moyenne entre deux allers-retours successifs. Le résultat indique aussi la durée des mesures et le volume de données de test transféré dans chaque sens.

Pendant le test, l’écran indique l’étape en cours. Une fois les deux débits mesurés, les barres les comparent sur la même échelle. Si l’envoi atteint la moitié du débit en réception, sa barre est deux fois plus courte.

Chaque essai transfère jusqu’à 100 Mo de données de test, envoi et réception compris. Les données envoyées sont générées en mémoire. Vous pouvez cliquer sur **Annuler** ou quitter la page pour arrêter la mesure. Si un transfert échoue, les mesures déjà terminées restent affichées et **Relancer le test** démarre un nouvel essai.

## Consulter l’historique

L’**historique** conserve les dix derniers tests terminés dans votre profil Windows, même après fermeture de Toolbox. Chaque entrée indique la date, l’heure et la liaison du test (Wi-Fi, Ethernet ou autre type identifié par Windows). Pour un test Wi-Fi, le nom du réseau (SSID) apparaît à côté de la date lorsque Windows le fournit. Ce nom est lu sur la carte utilisée et conservé sur le PC.

Ouvrez **Détails de la connexion et des transferts**, ou dépliez une ligne de l’historique, pour consulter séparément l’IPv4 publique, l’IPv6 publique et le FAI. Si le serveur de mesure fournit une seule adresse, Toolbox interroge le service icanhazip de Cloudflare pour l’autre famille. Une famille inaccessible apparaît comme « indisponible ». Les adresses correspondent aux sorties Internet du PC au moment du test. IPv4 et IPv6 peuvent emprunter des cartes différentes ou passer par un VPN ou un proxy. Les anciens tests affichent l’adresse enregistrée à l’époque.

[Retour au catalogue](../../README.md)
