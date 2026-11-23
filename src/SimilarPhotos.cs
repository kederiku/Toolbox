using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AtelierPC
{
    internal static class SimilarPhotos
    {
        internal const int Side = 24;
        const int Pixels = Side * Side;

        sealed class Cluster
        {
            internal readonly DuplicateGroup Group = new DuplicateGroup();
            internal readonly List<DuplicateFile> Representatives = new List<DuplicateFile>();

            internal bool Accepts(DuplicateFile file, CancellationToken token)
            {
                foreach (var member in Representatives)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Close(file, member)) return false;
                }
                return true;
            }
        }

        // Three regional brightness buckets limit candidate comparisons while allowing an adjacent
        // bucket in each region. Final membership always compares premultiplied color AND opacity.
        static bool Describe(byte[] sample, out int[] regions)
        {
            regions = new int[4];
            var sums = new int[4];
            var squares = new long[4];
            for (int y = 0; y < Side; y++)
                for (int x = 0; x < Side; x++)
                {
                    int i = (y * Side + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        int value = sample[i + c];
                        sums[c] += value;
                        squares[c] += value * value;
                    }
                    regions[(y < Side / 2 ? 0 : 2) + (x < Side / 2 ? 0 : 1)] +=
                        (sample[i] + 2 * sample[i + 1] + sample[i + 2]) / 4;
                }
            for (int q = 0; q < 4; q++) regions[q] /= 16 * Pixels / 4;
            for (int c = 0; c < 4; c++)
                if (squares[c] * Pixels - (long)sums[c] * sums[c] >= 144L * Pixels * Pixels) return true;
            return false; // A uniform field has too little spatial information to recognize its subject.
        }

        static bool Close(DuplicateFile a, DuplicateFile b)
        {
            double aspectA = (double)a.Width / a.Height, aspectB = (double)b.Width / b.Height;
            if (Math.Max(aspectA, aspectB) > 1.005 * Math.Min(aspectA, aspectB)) return false;
            var errors = new int[4];
            int outliers = 0;
            for (int by = 0; by < Side; by += 4)
                for (int bx = 0; bx < Side; bx += 4)
                {
                    int blockError = 0;
                    for (int y = by; y < by + 4; y++)
                        for (int x = bx; x < bx + 4; x++)
                        {
                            int i = (y * Side + x) * 4;
                            int largest = 0;
                            for (int c = 0; c < 4; c++)
                            {
                                int delta = Math.Abs(a.VisualSample[i + c] - b.VisualSample[i + c]);
                                errors[c] += delta;
                                largest = Math.Max(largest, delta);
                            }
                            if (largest > 48) return false;
                            if (largest > 16 && ++outliers > Pixels / 50) return false;
                            blockError += largest;
                        }
                    if (blockError > 16 * 12) return false;
                }
            return errors[0] <= 5 * Pixels && errors[1] <= 5 * Pixels &&
                errors[2] <= 5 * Pixels && errors[3] <= 2 * Pixels;
        }

        internal static List<DuplicateGroup> Group(IEnumerable<DuplicateGroup> exactFamilies, CancellationToken token)
        {
            var clusters = new List<Cluster>();
            var buckets = new Dictionary<Tuple<string, int, int, int>, List<Cluster>>();
            foreach (var family in exactFamilies)
            {
                token.ThrowIfCancellationRequested();
                var file = family.Files.FirstOrDefault(f => f.Width > 0 && f.Height > 0 && f.VisualSample != null);
                if (file == null || file.VisualSample.Length != 4 * Pixels || file.ContentStreamHash == null) continue;
                int[] region;
                if (!Describe(file.VisualSample, out region)) continue;
                Cluster selected = null;
                for (int x = region[0] - 1; x <= region[0] + 1 && selected == null; x++)
                    for (int y = region[1] - 1; y <= region[1] + 1 && selected == null; y++)
                        for (int z = region[2] - 1; z <= region[2] + 1 && selected == null; z++)
                        {
                            List<Cluster> candidates;
                            if (buckets.TryGetValue(Tuple.Create(file.ContentStreamHash, x, y, z), out candidates))
                                selected = candidates.FirstOrDefault(cluster => cluster.Accepts(file, token));
                        }
                if (selected == null)
                {
                    selected = new Cluster();
                    clusters.Add(selected);
                    var key = Tuple.Create(file.ContentStreamHash, region[0], region[1], region[2]);
                    List<Cluster> bucket;
                    if (!buckets.TryGetValue(key, out bucket)) buckets.Add(key, bucket = new List<Cluster>());
                    bucket.Add(selected);
                }
                selected.Group.Files.AddRange(family.Files);
                selected.Representatives.Add(file);
            }
            return clusters.Where(c => c.Representatives.Count >= 2).Select(c => c.Group).ToList();
        }
    }
}
