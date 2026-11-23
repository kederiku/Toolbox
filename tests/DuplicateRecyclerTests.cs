using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static DuplicateFile RecycleFixture(string directory, string name, string content)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, content);
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return new DuplicateFile { FullPath = path, Length = stream.Length,
                    LastWriteUtc = File.GetLastWriteTimeUtc(path), ByteHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") };
        }

        static DuplicateRecycleResult RecycleSelection(DuplicateGroup group, Action<string> move,
            CancellationToken token, params DuplicateFile[] files)
        {
            return DuplicateRecycler.Apply(new[] { group }, new HashSet<string>(files.Select(f => f.FullPath)),
                token, (path, verify) => { verify(); move(path); });
        }

        static void CheckDuplicateRecycler()
        {
            var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-corbeille-" + Guid.NewGuid().ToString("N")));
            try
            {
                var first = RecycleFixture(temp.FullName, "choisi.txt", "copie");
                var second = RecycleFixture(temp.FullName, "second.txt", "copie");
                var keeper = RecycleFixture(temp.FullName, "conserver.txt", "copie");
                var group = new DuplicateGroup { SameBytes = true };
                group.Files.AddRange(new[] { first, second, keeper });
                var token = CancellationToken.None;
                string basket = temp.CreateSubdirectory("destination").FullName;
                Action<string> move = path => File.Move(path, Path.Combine(basket, Path.GetFileName(path)));
                var result = RecycleSelection(group, move, token, first, second, keeper);
                Check(result.Moved.Count == 0 && result.Issues.Count == 3 && group.Files.All(f => File.Exists(f.FullPath)),
                    "Cocher tout le groupe par programmation préserve tous les exemplaires");

                File.WriteAllText(first.FullPath, "autre");
                File.SetLastWriteTimeUtc(first.FullPath, first.LastWriteUtc);
                result = RecycleSelection(group, move, token, first);
                Check(result.Moved.Count == 0 && result.Issues.Single().Contains(first.FullPath) && File.Exists(first.FullPath),
                    "Une modification du contenu à taille et date identiques est détectée");

                File.WriteAllText(first.FullPath, "copie");
                File.SetLastWriteTimeUtc(first.FullPath, first.LastWriteUtc);
                File.Delete(second.FullPath);
                File.WriteAllText(keeper.FullPath, "autre");
                File.SetLastWriteTimeUtc(keeper.FullPath, keeper.LastWriteUtc);
                result = RecycleSelection(group, move, token, first);
                Check(result.Moved.Count == 0 && result.Issues.Single().Contains(first.FullPath) && File.Exists(first.FullPath),
                    "Les conservés disparus ou modifiés ne permettent aucun déplacement");

                File.WriteAllText(keeper.FullPath, "copie");
                File.SetLastWriteTimeUtc(keeper.FullPath, keeper.LastWriteUtc);
                bool writeBlocked = false;
                result = RecycleSelection(group, path => {
                    try { using (File.OpenWrite(keeper.FullPath)) { } }
                    catch (IOException) { writeBlocked = true; }
                    move(path);
                }, token, first);
                Check(result.Moved.SequenceEqual(new[] { first.FullPath }) && result.Issues.Count == 0 && writeBlocked &&
                    File.Exists(Path.Combine(basket, "choisi.txt")) && File.Exists(keeper.FullPath),
                    "Le conservé valide suivant est protégé pendant le déplacement");

                var photo = RecycleFixture(temp.FullName, "photo.png", "autres octets");
                var next = RecycleFixture(temp.FullName, "conversion.webp", "conversion");
                var mixed = new DuplicateGroup { SameBytes = false };
                mixed.Files.AddRange(new[] { photo, next, keeper });
                result = RecycleSelection(mixed, path => {
                    move(path);
                    File.WriteAllText(next.FullPath, "modifiée");
                }, token, photo, next);
                Check(result.Moved.SequenceEqual(new[] { photo.FullPath }) && result.Issues.Single().Contains(next.FullPath) &&
                    File.Exists(next.FullPath), "Chaque conversion est revérifiée juste avant son tour avec sa propre empreinte");

                var refused = RecycleFixture(temp.FullName, "refus.txt", "copie");
                var accepted = RecycleFixture(temp.FullName, "accepte.txt", "copie");
                var pending = RecycleFixture(temp.FullName, "attente.txt", "copie");
                group.Files.Clear();
                group.Files.AddRange(new[] { refused, accepted, pending, keeper });
                using (var cancel = new CancellationTokenSource())
                {
                    result = RecycleSelection(group, path => {
                        if (path == refused.FullPath) throw new IOException("Corbeille indisponible.");
                        move(path);
                        cancel.Cancel();
                    }, cancel.Token, refused, accepted, pending);
                }
                Check(result.Moved.SequenceEqual(new[] { accepted.FullPath }) && result.Issues.Count == 2 &&
                    result.Issues.Any(s => s.Contains(refused.FullPath)) && result.Issues.Any(s => s.Contains(pending.FullPath)) &&
                    File.Exists(refused.FullPath) && File.Exists(pending.FullPath) && File.Exists(keeper.FullPath),
                    "Le bilan distingue les déplacements aboutis, le refus de la Corbeille et les copies annulées");

                bool renameBlocked = false;
                result = DuplicateRecycler.Apply(new[] { group }, new HashSet<string> { refused.FullPath },
                    token, (path, verify) => {
                        string otherName = Path.Combine(temp.FullName, "remplacement.txt");
                        try { File.Move(path, otherName); File.Move(otherName, path); }
                        catch (IOException) { renameBlocked = true; }
                        verify();
                        move(path);
                    });
                Check(renameBlocked && result.Moved.SequenceEqual(new[] { refused.FullPath }),
                    "Le nom de la copie reste protégé jusqu’à ce que la Corbeille soit prête à la recevoir");

                result = RecycleSelection(group, path => { throw new NotImplementedException(); }, token, pending);
                Check(result.Moved.Count == 0 && result.Issues.Single().Contains(pending.FullPath) && File.Exists(pending.FullPath),
                    "Une Corbeille non prise en charge par le fournisseur Shell est signalée pour le fichier concerné");

                if (new DriveInfo(Path.GetPathRoot(temp.FullName)).DriveFormat == "NTFS")
                {
                    CheckDownloadStreamRecycle(temp, basket, move, token);
                    foreach (bool changeKeeper in new[] { false, true })
                    {
                        var folder = temp.CreateSubdirectory(changeKeeper ? "flux sur le conservé" : "flux sur la sélection");
                        File.WriteAllText(Path.Combine(folder.FullName, "A.txt"), "fichier commun");
                        File.Copy(Path.Combine(folder.FullName, "A.txt"), Path.Combine(folder.FullName, "B.txt"));
                        WriteScanStream(Path.Combine(folder.FullName, "A.txt"), 1, "Zone.Identifier");
                        var scanned = DuplicateScanner.Scan(folder.FullName, token, null).Groups.Single();
                        Check(scanned.Files.Count(f => f.HasDownloadInfo) == 1,
                            "La recherche regroupe les copies avec et sans provenance de téléchargement");
                        var selected = scanned.Files[0];
                        var changed = changeKeeper ? scanned.Files[1] : selected;
                        WriteScanStream(changed.FullPath, 7);
                        File.SetLastWriteTimeUtc(changed.FullPath, changed.LastWriteUtc);
                        result = RecycleSelection(scanned, move, token, selected);
                        Check(result.Moved.Count == 0 && result.Issues.Single().Contains(selected.FullPath) &&
                            scanned.Files.All(f => File.Exists(f.FullPath)),
                            "Un flux NTFS ajouté après l’analyse sur " + (changeKeeper ? "le conservé" : "la sélection") + " empêche le déplacement");
                    }
                }
            }
            finally { temp.Delete(true); }
        }

        static void CheckDownloadStreamRecycle(DirectoryInfo temp, string basket, Action<string> move, CancellationToken token)
        {
            var folder = temp.CreateSubdirectory("téléchargements");
            string first = Path.Combine(folder.FullName, "copie A.txt");
            string second = Path.Combine(folder.FullName, "copie B.txt");
            string annotated = Path.Combine(folder.FullName, "copie annotée.txt");
            File.WriteAllText(first, "contenu commun");
            WriteScanStream(first, 9);
            File.Copy(first, second);
            WriteScanStream(first, 1, "zone.identifier");
            WriteScanStream(second, 2, "Zone.Identifier");
            File.Copy(first, annotated);
            WriteScanStream(annotated, 5, "Zone.Identifier.backup");
            File.Copy(annotated, Path.Combine(folder.FullName, "sauvegarde annotée.txt"));
            var scan = DuplicateScanner.Scan(folder.FullName, token, null);
            var group = scan.Groups.SingleOrDefault(g => g.Files.Any(f => f.FullPath == first));
            Check(scan.Groups.Count == 2 && group != null && group.SameBytes && group.Files.Count == 2 &&
                group.Files.Any(f => f.FullPath == second),
                "Les provenances différentes permettent de sélectionner les copies, le flux Zone.Identifier.backup reste distinct");
            var selected = group.Files.Single(f => f.FullPath == first);
            var keeper = group.Files.Single(f => f.FullPath == second);

            var invalidGroup = new DuplicateGroup { SameBytes = true };
            invalidGroup.Files.Add(selected);
            invalidGroup.Files.Add(scan.Groups.SelectMany(g => g.Files).Single(f => f.FullPath == annotated));
            var incompatible = RecycleSelection(invalidGroup, move, token, selected);
            Check(incompatible.Moved.Count == 0 && incompatible.Issues.Count == 1 && File.Exists(first),
                "Des flux complémentaires différents empêchent le déplacement même si le groupe est formé par programmation");

            foreach (var changed in new[] { selected, keeper })
            {
                bool changedBeforeMove = false;
                var refused = DuplicateRecycler.Apply(new[] { group }, new HashSet<string> { first }, token,
                    (path, verify) => {
                        WriteScanStream(changed.FullPath, 3, "Zone.Identifier");
                        changedBeforeMove = true;
                        verify();
                        move(path);
                    });
                Check(changedBeforeMove && refused.Moved.Count == 0 && refused.Issues.Count == 1 &&
                    File.Exists(first) && File.Exists(second), "Une provenance modifiée juste avant le déplacement est détectée sur " + changed.FullPath);
                WriteScanStream(changed.FullPath, (byte)(changed == selected ? 1 : 2), "Zone.Identifier");
                File.SetLastWriteTimeUtc(changed.FullPath, changed.LastWriteUtc);
            }

            var result = RecycleSelection(group, move, token, selected);
            string destination = Path.Combine(basket, Path.GetFileName(first));
            Check(result.Moved.SequenceEqual(new[] { first }) && result.Issues.Count == 0 && File.Exists(destination) &&
                File.Exists(second) && !File.Exists(first) && selected.HasDownloadInfo && keeper.HasDownloadInfo &&
                selected.StreamHash != keeper.StreamHash &&
                DuplicateStreams.Fingerprint(destination, token) == selected.StreamHash &&
                DuplicateStreams.Fingerprint(second, token) == keeper.StreamHash,
                "La copie sélectionnée est déplacée et chaque exemplaire conserve ses propres flux NTFS");
        }
    }
}
