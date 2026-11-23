namespace AtelierPC
{
    internal enum Destination { Manager, Updates }

    internal sealed class Advice
    {
        public readonly string Title, Cause;
        public readonly string[] Steps;
        public readonly Destination Destination;
        Advice(string title, string cause, Destination destination, params string[] steps)
        {
            Title = title; Cause = cause; Destination = destination; Steps = steps;
        }

        const string Restart = "Enregistrez votre travail, puis choisissez Démarrer › Marche/Arrêt › Redémarrer.";
        const string ScanAgain = "Revenez dans Toolbox et relancez l’analyse.";
        const string Update = "Ouvrez Windows Update et recherchez des mises à jour. Consultez aussi les mises à jour facultatives de pilotes.";
        const string Maker = "Si le problème persiste, recherchez le pilote correspondant au modèle exact sur le site du fabricant du PC ou du périphérique.";
        const string Properties = "Dans le Gestionnaire de périphériques, retrouvez cet appareil et double-cliquez sur son nom pour lire son état.";

        static Advice Driver(string title, string cause)
        {
            return new Advice(title, cause, Destination.Updates, Update, Maker, ScanAgain);
        }

        public static Advice For(int code)
        {
            switch (code)
            {
                case 1:
                    return Driver(
                        "Configuration incomplète",
                        "Le logiciel qui relie l’appareil à Windows, appelé pilote, est manquant ou mal configuré.");
                case 3:
                    return new Advice(
                        "Le pilote ne peut pas fonctionner",
                        "Le pilote peut être endommagé ou le PC peut manquer de mémoire disponible.",
                        Destination.Updates,
                        "Fermez les applications inutilisées, puis réessayez d’utiliser l’appareil.",
                        Update,
                        ScanAgain);
                case 9:
                    return Driver(
                        "Appareil mal identifié",
                        "Les informations envoyées par l’appareil ne permettent pas à Windows de l’identifier correctement.");
                case 10:
                    return Driver(
                        "L’appareil ne démarre pas",
                        "Le lancement du périphérique a échoué. Son pilote ou l’appareil lui-même peut être en cause.");
                case 12:
                case 16:
                    return new Advice(
                        "Ressources matérielles indisponibles",
                        "Windows ne peut pas attribuer à l’appareil toutes les ressources nécessaires pour fonctionner.",
                        Destination.Manager,
                        Restart,
                        Properties,
                        "Si le problème persiste, contactez le fabricant avec le code Windows indiqué.");
                case 14:
                case 38:
                    return new Advice(
                        "Redémarrage nécessaire",
                        code == 14 ? "Windows doit terminer la configuration de cet appareil au prochain démarrage." : "Une ancienne copie du pilote est encore en mémoire et empêche son chargement.",
                        Destination.Manager,
                        Restart,
                        ScanAgain);
                case 18:
                    return Driver(
                        "Pilote à réinstaller",
                        "Windows demande une nouvelle installation du logiciel qui commande cet appareil.");
                case 19:
                case 40:
                    return Driver(
                        "Configuration du pilote endommagée",
                        "Windows signale que les réglages enregistrés pour ce pilote sont incomplets ou endommagés.");
                case 21:
                case 54:
                    return new Advice(
                        "Appareil en cours de transition",
                        code == 21 ? "Windows est en train de retirer cet appareil de sa configuration." : "Windows est en train de réinitialiser l’appareil.",
                        Destination.Manager,
                        "Patientez quelques instants pour laisser Windows terminer.",
                        ScanAgain);
                case 22:
                    return new Advice(
                        "Périphérique désactivé",
                        "L’utilisation de cet appareil a été désactivée dans Windows. Cela peut être un choix volontaire.",
                        Destination.Manager,
                        "Pour remettre l’appareil en service, choisissez « Réactiver cet appareil » si le bouton est proposé.",
                        "Vous pouvez aussi ouvrir le Gestionnaire de périphériques, faire un clic droit sur cet appareil et choisir « Activer l’appareil ».",
                        ScanAgain);
                case 24:
                case 41:
                case 45:
                    return new Advice(
                        "Appareil introuvable ou indisponible",
                        "Windows connaît cet appareil mais ne parvient pas à communiquer avec lui. Le branchement ou le pilote peut être en cause.",
                        Destination.Manager,
                        "Pour un appareil externe, vérifiez son alimentation et son câble. Essayez un autre port si possible.",
                        Properties,
                        ScanAgain);
                case 28:
                    return Driver(
                        "Aucun pilote installé",
                        "Il manque le logiciel qui permet à Windows de communiquer avec cet appareil.");
                case 29:
                    return new Advice(
                        "Appareil désactivé par le matériel",
                        "Le programme de démarrage du PC ne met pas les ressources de cet appareil à disposition de Windows.",
                        Destination.Manager,
                        "Consultez la notice du PC ou demandez au fabricant comment activer cet appareil sur votre modèle.",
                        ScanAgain);
                case 31:
                case 37:
                case 39:
                    return Driver(
                        "Le pilote ne se charge pas",
                        "Windows ne parvient pas à charger ou à démarrer le logiciel qui fait fonctionner cet appareil.");
                case 32:
                    return Driver(
                        "Service du pilote désactivé",
                        "Le composant dont Windows a besoin pour démarrer ce pilote a été désactivé.");
                case 33:
                    return new Advice(
                        "Besoins de l’appareil inconnus",
                        "Windows ne parvient pas à déterminer quelles ressources matérielles fournir à cet appareil.",
                        Destination.Manager,
                        "Relevez le nom de l’appareil et le code 33, puis demandez au fabricant de vérifier sa compatibilité avec votre PC.");
                case 34:
                    return new Advice(
                        "Réglage manuel nécessaire",
                        "Windows ne peut pas déterminer automatiquement les réglages matériels de cet appareil.",
                        Destination.Manager,
                        "Consultez la notice du modèle. Le fabricant peut indiquer les réglages nécessaires.",
                        Properties);
                case 35:
                    return new Advice(
                        "Informations de démarrage incomplètes",
                        "Le programme de démarrage du PC ne fournit pas à Windows toutes les informations nécessaires pour configurer l’appareil.",
                        Destination.Manager,
                        "Contactez le fabricant du PC avec son modèle exact pour vérifier si une mise à jour du programme de démarrage est prévue.");
                case 36:
                    return new Advice(
                        "Conflit de communication matérielle",
                        "L’appareil et le PC ne sont pas configurés pour utiliser le même type de signal matériel.",
                        Destination.Manager,
                        "Demandez au fabricant de vérifier les réglages matériels adaptés à votre modèle de PC.");
                case 42:
                    return new Advice(
                        "Appareil détecté en double",
                        "Windows a trouvé deux entrées pour le même appareil et ne parvient pas à charger son pilote.",
                        Destination.Manager,
                        Restart,
                        ScanAgain);
                case 44:
                    return new Advice(
                        "Appareil arrêté par un logiciel",
                        "Une application ou un service de Windows a demandé l’arrêt de cet appareil.",
                        Destination.Manager,
                        Restart,
                        ScanAgain);
                case 46:
                    return new Advice(
                        "Arrêt de Windows en cours",
                        "Windows libère les appareils pendant l’arrêt du PC.",
                        Destination.Manager,
                        "Au prochain démarrage du PC, relancez l’analyse.");
                case 43:
                    return new Advice(
                        "L’appareil a signalé un problème",
                        "Le pilote a indiqué un dysfonctionnement et Windows a arrêté l’appareil. Le code seul ne permet pas de savoir quelle pièce ou quel logiciel est en cause.",
                        Destination.Updates,
                        "Pour un appareil externe, vérifiez son alimentation et son branchement. Pour un disque, éjectez-le avant de le débrancher.",
                        Update,
                        Maker);
                case 47:
                    return new Advice(
                        "Appareil prêt à être retiré",
                        "Windows a préparé l’appareil pour son éjection. Il attend maintenant son débranchement.",
                        Destination.Manager,
                        "Si vous avez demandé l’éjection, débranchez l’appareil, puis rebranchez-le pour le réutiliser.",
                        ScanAgain);
                case 48:
                case 52:
                    return Driver(
                        "Pilote bloqué par Windows",
                        code == 52 ? "Windows ne peut pas vérifier la signature qui identifie l’éditeur du pilote." : "Ce pilote est connu pour poser des problèmes avec Windows.");
                case 49:
                    return new Advice(
                        "Limite de configuration atteinte",
                        "La partie de Windows qui conserve les réglages du système est pleine. Elle ne peut plus enregistrer de nouveaux appareils.",
                        Destination.Manager,
                        "Demandez de l’aide pour repérer les anciennes installations de matériel qui peuvent être retirées. Indiquez le code 49.");
                case 50:
                    return Driver(
                        "Réglages de l’appareil incomplets",
                        "Windows n’a pas réussi à appliquer tous les paramètres nécessaires à l’appareil.");
                case 51:
                    return new Advice(
                        "En attente d’un autre appareil",
                        "Windows attend qu’un autre composant démarre avant de pouvoir utiliser cet appareil.",
                        Destination.Manager,
                        "Vérifiez si l’analyse signale un problème sur un autre périphérique.",
                        Restart,
                        ScanAgain);
                case 53:
                    return new Advice(
                        "Appareil réservé au diagnostic de Windows",
                        "Un outil de diagnostic avancé de Windows utilise cet appareil pendant la session.",
                        Destination.Manager,
                        "Demandez à la personne qui a configuré le PC si cet outil de diagnostic est encore nécessaire.");
                case 55:
                    return new Advice(
                        "Ouverture de session nécessaire",
                        "Les règles de sécurité du PC empêchent cet appareil de démarrer lorsque la session est verrouillée.",
                        Destination.Manager,
                        "Ouvrez votre session sur le PC et rebranchez l’appareil externe si nécessaire.",
                        ScanAgain);
                case 57:
                    return new Advice(
                        "Appareil indisponible dans la machine virtuelle",
                        "Le PC qui héberge la machine virtuelle n’a pas réussi à lui attribuer cet appareil.",
                        Destination.Manager,
                        "Transmettez le nom de l’appareil et le code 57 à la personne qui gère la machine virtuelle.");
                case 56:
                    return new Advice(
                        "Configuration en cours",
                        "Windows doit encore terminer la configuration de cette famille de périphériques.",
                        Destination.Manager,
                        "Patientez un instant puis relancez l’analyse.",
                        Restart,
                        ScanAgain);
                case Device.Unreadable:
                    return new Advice(
                        "État indéterminé",
                        "Windows n’a pas pu lire l’état de cet appareil pendant l’analyse. Il a pu être débranché entre-temps.",
                        Destination.Manager,
                        "Vérifiez que l’appareil est connecté et allumé.",
                        ScanAgain);
                default:
                    return new Advice(
                        "Windows signale un problème",
                        "Le système a signalé une anomalie. Il faut consulter les détails de l’appareil pour en préciser la cause.",
                        Destination.Manager,
                        Properties,
                        "Si vous avez besoin d’aide, indiquez au fabricant le nom de l’appareil et le code Windows affiché ici.");
            }
        }

        public static Advice For(Device device)
        {
            UsbFailure failure = UsbEvidence.ForDevice(device);
            if (failure == UsbFailure.None) return For(device.Code);

            string title, cause;
            switch (failure)
            {
                case UsbFailure.Reset:
                    title = "Windows n’arrive pas à réinitialiser le port USB";
                    cause = "Le PC a détecté un branchement. La communication s’est interrompue pendant la remise en service du port USB. Windows peut alors ignorer jusqu’au nom de l’appareil.";
                    break;
                case UsbFailure.Description:
                    title = "Windows ne parvient pas à lire la description USB";
                    cause = "L’échange qui permet à l’appareil de transmettre sa description ou sa configuration a échoué. Un câble, un adaptateur ou l’appareil peut interrompre cet échange.";
                    break;
                case UsbFailure.Address:
                    title = "L’appareil ne reçoit pas son adresse USB";
                    cause = "Windows détecte le branchement, mais l’échange qui attribue une adresse à l’appareil échoue. Le PC ne peut pas poursuivre sa configuration.";
                    break;
                default:
                    title = "L’identification USB s’est interrompue";
                    cause = "Le PC détecte un branchement mais ne reçoit pas toutes les informations nécessaires pour reconnaître l’appareil.";
                    break;
            }
            return new Advice(title, cause, Destination.Manager,
                UsbEvidence.PhysicalEntry(device)
                    ? "Utilisez « Retrouver cet appareil » pour tester un accessoire externe à la fois. Pour un disque, terminez les transferts et éjectez-le d’abord."
                    : "Retirez un seul accessoire externe et relancez l’analyse. Si l’alerte disparaît, rebranchez-le sur la même prise et vérifiez si elle revient. Pour un disque, terminez les transferts et éjectez-le d’abord.",
                "Une fois l’accessoire repéré, gardez le même câble et essayez une autre prise directement sur le PC. S’il fonctionne, retestez la prise ou le concentrateur d’origine avec un accessoire qui fonctionne.",
                "Si l’erreur suit l’accessoire, gardez cette prise et essayez un câble de données qui fonctionne avec un autre appareil. Vérifiez aussi l’alimentation externe de l’accessoire s’il en a une.",
                "Si l’accessoire échoue aussi sur un second PC avec un câble éprouvé, faites vérifier l’accessoire. Si l’alerte persiste alors que les accessoires externes sont retirés, un composant USB interne peut être concerné. Copiez le résumé pour le support du PC.");
        }
    }
}
