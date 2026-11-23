using System;
using System.Linq;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckUsbSerialPorts()
        {
            const string adapterId = @"USB\VID_0403&PID_6010\PRIVE-A";
            const string twinId = @"USB\VID_0403&PID_6010\PRIVE-B";
            var adapter = new Device(adapterId, "Adaptateur de Camille", 0, comPort: "com10");
            var twin = new Device(twinId, adapter.Name, 0, comPort: "COM7");
            var function = new Device(@"USB\VID_0403&PID_6010&MI_00\1", "Interface privée", 0,
                parentId: adapter.Id.ToLowerInvariant());
            var port = new Device(@"FTDIBUS\PORT\1", "Nom privé", 0, parentId: function.Id, comPort: "COM2");
            var repeated = new Device(@"USB\VID_0403&PID_6010&MI_01\2", "Interface", 0,
                parentId: adapter.Id, comPort: "COM10");
            var snapshot = new[] { twin, function, port, repeated, adapter };
            var ports = UsbSerialPorts.Read(snapshot);
            string first = ports.Describe(adapter);
            Check(first.Contains("COM2, COM10") && first.Contains("interface") && !first.Contains("COM7"),
                "Plusieurs affectations d’un même appareil sans historique ni propriétés USB, dans l’ordre numérique");
            Check(!first.Contains("PRIVE") && !first.Contains("Camille") && !first.Contains("Nom privé"),
                "Le bilan série contient uniquement les numéros COM et les indications génériques");
            Check(ports.Describe(twin).Contains("COM7") && !ports.Describe(twin).Contains("COM10"),
                "Deux exemplaires du même modèle gardent chacun leurs ports");
            var high = new Device(twinId, twin.Name, 0, comPort: "COM4096");
            Check(UsbSerialPorts.Read(new[] { high }).Describe(high).Contains("COM4096"),
                "Le dernier numéro accepté par l’arbitre COM Windows reste consultable");
            Check(ports.Describe(function) == "" && ports.Describe(null) == "",
                "Aucune attribution à une entrée qui n’est pas la fiche de l’appareil");
            Check(!UsbPortRecord.Read(snapshot).Any(), "Un relevé sans propriétés USB ne crée aucune installation à nettoyer");

            var receiver = new Device(twinId, twin.Name, 0);
            var badId = new Device(@"FTDIBUS\PORT\", "Entrée incomplète", 0, parentId: receiver.Id, comPort: "COM5");
            var wrongModel = new Device(@"USB\VID_1111&PID_2222&MI_00\3", "Incohérent", 0, parentId: receiver.Id, comPort: "COM8");
            var missing = new Device(@"FTDIBUS\PORT\3", "Orphelin", 0, parentId: @"USB\ABSENT\1", comPort: "COM9");
            var cycle = new Device(@"FTDIBUS\PORT\4", "Boucle", 0, parentId: @"FTDIBUS\PORT\4", comPort: "COM11");
            foreach (var invalid in new[] { "COM0", "COM01", "COM7\nSECRET", "COM8\n", "COM９", "LPT1", "COM4097", "COM123456789012345678" })
            {
                var badPort = new Device(@"FTDIBUS\PORT\5", "Entrée", 0, parentId: receiver.Id, comPort: invalid);
                Check(UsbSerialPorts.Read(new[] { receiver, badPort, badId, wrongModel, missing, cycle }).Describe(receiver) == "",
                    "Une interface sans rattachement fiable ou sans numéro COM valide est ignorée");
            }
            var duplicate = new Device(function.Id.ToLowerInvariant(), "Interface", 0, parentId: twin.Id);
            var plain = new Device(adapter.Id, adapter.Name, 0);
            var ambiguous = UsbSerialPorts.Read(new[] { plain, receiver, function, duplicate, port });
            Check(ambiguous.Describe(plain) == "" && ambiguous.Describe(receiver) == "",
                "Un même identifiant d’interface rattaché à deux parents bloque l’attribution");
            var ambiguousOwner = UsbSerialPorts.Read(new[] { plain, adapter, function, port });
            Check(ambiguousOwner.Describe(plain) == "" && ambiguousOwner.Describe(adapter) == "",
                "Deux entrées USB de même identifiant ne reçoivent aucune affectation");
            var radio = new Device(@"USB\VID_0403&PID_0001\RADIO", "Radio USB", 0);
            var wirelessPort = new Device(@"BTHENUM\{00001101-0000-1000-8000-00805f9b34fb}\1", "Port distant", 0,
                parentId: radio.Id, comPort: "COM4");
            Check(UsbSerialPorts.Read(new[] { wirelessPort, radio }).Describe(radio) == "",
                "Un service série Bluetooth distant ne devient pas un port de l’adaptateur USB local");
            var sharing = new Device(twinId, twin.Name, 0, comPort: "COM2");
            var shared = UsbSerialPorts.Read(new[] { adapter, function, port, sharing });
            Check(shared.Describe(adapter).Contains("aussi") && shared.Describe(sharing).Contains("aussi"),
                "Un numéro relevé sur deux appareils distincts est une piste pour les deux fiches");

            var evidence = new UsbEvidence(null, "PCIROOT(0)#USBROOT(0)#USB(3)", null, null, false);
            var old = new Device(twinId, twin.Name, 0, usb: evidence, comPort: "COM7\nSECRET");
            var moved = new Device(adapterId, adapter.Name, 0,
                usb: new UsbEvidence(null, "PCIROOT(0)#USBROOT(0)#USB(4)", null, null, false), comPort: "COM10");
            var change = new UsbTracker().Update(new[] { moved }, new[] { old }).Single();
            Check(change.Settings.Contains("COM10") && !change.Settings.Contains("SECRET") && !change.CanClean,
                "Une ancienne affectation mal formée est écartée du compte rendu et du retrait");
        }
    }
}
