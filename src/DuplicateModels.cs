using System;
using System.Collections.Generic;

namespace AtelierPC
{
    internal sealed class DuplicateFile
    {
        internal string FullPath, ByteHash, Format;
        internal string StreamHash = "";
        internal string ContentStreamHash = "";
        internal bool HasDownloadInfo;
        internal long Length;
        internal DateTime LastWriteUtc;
        internal int Width, Height;
        internal byte[] Thumbnail, VisualSample;
    }

    internal sealed class DuplicateGroup
    {
        internal readonly List<DuplicateFile> Files = new List<DuplicateFile>();
        internal bool SameBytes;
    }

    internal sealed class DuplicateScanResult
    {
        internal readonly List<DuplicateGroup> Groups = new List<DuplicateGroup>();
        internal readonly List<DuplicateGroup> SimilarGroups = new List<DuplicateGroup>();
        internal readonly List<string> Issues = new List<string>();
        internal int Scanned;
    }

    internal sealed class DuplicateRecycleResult
    {
        internal readonly List<string> Moved = new List<string>();
        internal readonly List<string> Issues = new List<string>();
    }
}
