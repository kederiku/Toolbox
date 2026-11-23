using System;
using System.Collections.Generic;
using System.Linq;

namespace AtelierPC
{
    internal enum UsbStage { Unplug, Replug, Done }

    internal sealed class UsbProbe
    {
        readonly Device target;
        readonly HashSet<string> initial;
        HashSet<string> unplugged;
        public UsbStage Stage { get; private set; }
        public string Detail { get; private set; }
        public string Report { get; private set; }

        public UsbProbe(Device target, IEnumerable<Device> baseline)
        {
            this.target = target;
            var snapshot = baseline.ToList();
            initial = Ids(snapshot.Where(UsbEvidence.PhysicalEntry));
            Stage = UsbStage.Unplug;
            Detail = "Choisissez un seul accessoire USB externe. Gardez les autres branchés, y compris le concentrateur. Pour un disque, terminez les transferts et éjectez-le d’abord. Débranchez cet accessoire, patientez quelques secondes puis cliquez sur « Vérifier le débranchement ».";
            if (HasUncertainIdentities(snapshot))
                Finish("Inventaire initial incomplet : certaines entrées USB ne sont pas identifiables.",
                    "Windows n’a pas pu distinguer chaque entrée USB. Gardez les branchements en place et recommencez l’analyse.");
        }

        static bool HasUncertainIdentities(List<Device> snapshot)
        {
            return snapshot.Where(d => d.Kind == UsbKind.Device || d.Kind == UsbKind.Hub)
                .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .Any(g => string.IsNullOrWhiteSpace(g.Key) || g.Count() > 1);
        }

        static HashSet<string> Ids(IEnumerable<Device> devices)
        {
            return new HashSet<string>(devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        }

        void Finish(string observation, string next)
        {
            Stage = UsbStage.Done;
            Report = observation;
            Detail = next;
        }

        public void Observe(IEnumerable<Device> snapshot)
        {
            if (Stage == UsbStage.Done) return;
            var devices = snapshot.ToList();
            if (HasUncertainIdentities(devices))
            {
                Report = "Observation incomplète : Windows ne peut pas identifier chaque entrée USB.";
                Detail = Report + " Laissez les appareils en place et recommencez la vérification.";
                return;
            }
            var physical = devices.Where(UsbEvidence.PhysicalEntry).ToList();
            var current = Ids(physical);
            if (Stage == UsbStage.Unplug)
            {
                var stillPresent = physical.FirstOrDefault(d => string.Equals(d.Id, target.Id, StringComparison.OrdinalIgnoreCase));
                if (stillPresent != null)
                {
                    if (UsbEvidence.ForDevice(stillPresent) == UsbFailure.None)
                    {
                        Finish("L’état de l’entrée USB a changé avant que son retrait soit observé.",
                            "Windows voit toujours cette entrée, mais son état a changé. Consultez les nouveaux résultats et essayez l’appareil dans son application.");
                        return;
                    }
                    Detail = "L’entrée en erreur est toujours présente. Si vous venez de débrancher l’accessoire, attendez quelques secondes et vérifiez à nouveau. Sinon, rebranchez-le puis essayez un autre accessoire. Si aucun retrait ne change l’alerte, pensez aux composants intégrés, comme la webcam ou le Bluetooth.";
                    Report = "L’entrée en erreur est restée présente pendant l’essai de débranchement.";
                    return;
                }
                var expected = new HashSet<string>(initial, StringComparer.OrdinalIgnoreCase);
                expected.Remove(target.Id);
                if (!expected.SetEquals(current))
                {
                    Finish("Essai ambigu : plusieurs entrées USB ont changé.",
                        "Plusieurs appareils ont changé en même temps. Rebranchez-les comme au départ, puis recommencez en retirant un seul accessoire. Laissez la station d’accueil ou le concentrateur en place.");
                    return;
                }
                unplugged = current;
                Stage = UsbStage.Replug;
                Report = "L’entrée en erreur a disparu lors du retrait d’un seul accessoire. Rebranchement à vérifier.";
                Detail = "L’entrée en erreur a disparu quand vous avez retiré cet accessoire. Rebranchez uniquement ce même accessoire sur la même prise, avec le même câble. Attendez quelques secondes puis vérifiez son retour.";
                return;
            }

            var added = physical.Where(d => !unplugged.Contains(d.Id)).ToList();
            var matches = added.Where(d => UsbEvidence.SamePort(target, d) == true).ToList();
            if (added.Count > 1 || !unplugged.IsSubsetOf(current))
            {
                Finish("Essai ambigu : plusieurs entrées USB ont changé au rebranchement.",
                    "Impossible d’associer le retour à un seul accessoire. Remettez les branchements en place puis recommencez avec un seul appareil.");
                return;
            }
            if (matches.Count == 0)
            {
                if (added.Count == 1 && UsbEvidence.SamePort(target, added[0]) == null
                    && string.Equals(target.Id, added[0].Id, StringComparison.OrdinalIgnoreCase))
                {
                    string state = added[0].Code == 0 ? "Windows ne signale plus d’erreur sur cette entrée."
                        : added[0].Code > 0 ? "Windows signale le code " + added[0].Code + " sur cette entrée."
                        : "L’état de cette entrée reste à vérifier.";
                    Finish("La même entrée Windows est revenue. La prise physique reste à confirmer. " + state,
                        "Windows retrouve la même entrée sans fournir de repère de prise comparable. " + state + " Essayez la fonction habituelle de l’accessoire.");
                    return;
                }
                Report = "Disparition observée, retour sur la prise d’origine non confirmé.";
                Detail = "Aucun retour identifiable sur la prise d’origine. Vérifiez le branchement et l’alimentation, puis réessayez. Si un autre port a été utilisé, reprenez l’essai sur la prise de départ. Un appareil absent reste à vérifier.";
                return;
            }
            var returned = matches[0];
            if (returned.Code == 0)
            {
                string name = returned.Usb == null ? null : returned.Usb.Product;
                if (string.IsNullOrWhiteSpace(name) && returned.Name != returned.Id) name = returned.Name;
                string label = string.IsNullOrWhiteSpace(name) ? "" : " Windows l’appelle « " + name + " ».";
                Finish("Après retrait puis retour sur la prise suivie, Windows ne signale plus d’erreur sur l’entrée observée.",
                    "Windows détecte de nouveau un appareil sur la connexion suivie." + label + " Son état est normal. Essayez maintenant sa fonction habituelle, par exemple ouvrir la caméra ou lire un fichier.");
            }
            else if (returned.Code == Device.Unreadable)
                Finish("Retour observé, état illisible.", "L’appareil est revenu, mais Windows n’a pas encore pu lire son état. Patientez quelques secondes et relancez l’analyse.");
            else
            {
                string status = returned.Code > 0 ? "code " + returned.Code : "code non précisé";
                string next = UsbEvidence.ForDevice(returned) != UsbFailure.None
                    ? "Gardez le même câble et essayez une autre prise directement sur le PC. Si l’alerte suit l’appareil, essayez ensuite un câble de données éprouvé. Relancez l’analyse après chaque changement."
                    : "Consultez les étapes de résolution du nouveau signalement dans les résultats.";
                Finish("L’entrée en erreur a disparu au retrait et une erreur est revenue au rebranchement (" + status + ").",
                    "Le retrait puis le retour de l’alerte désignent le branchement que vous venez de tester (" + status + "). " + next);
            }
        }
    }
}
