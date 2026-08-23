using System;
using System.Threading.Tasks;

using OmegaAssetStudio2.Core.Packages.Compression;

namespace UpkManager.Compression
{
    /// <summary>
    /// The one file of this reader that is not the original.
    /// </summary>
    /// <remarks>
    /// Everything else under UpkManager is the reader as it stands in the tool
    /// this was taken from, copied and not rewritten. This file is the
    /// exception, and deliberately so: the original calls out to lzo2_64.dll,
    /// a binary this application does not ship and will not. What it did is
    /// done here by the packing and unpacking this application already carries,
    /// which is the same format written in managed code.
    /// <para>
    /// The class, the interface it answers to, and the names of everything on
    /// it are left exactly as they were, so that not one line of the code that
    /// calls it has to know.
    /// </para>
    /// </remarks>
    public sealed class LzoCompression : ILzoCompression
    {

        #region ILzoCompression Implementation

        public string Version => "managed lzo1x";

        public string VersionDate => string.Empty;

        public Task<byte[]> Compress(byte[] Source)
        {
            if (Source == null) throw new ArgumentNullException(nameof(Source));

            return Task.FromResult(Lzo1xCompressor.Compress(Source));
        }

        public void Decompress(byte[] Source, byte[] Destination)
        {
            if (Source == null) throw new ArgumentNullException(nameof(Source));
            if (Destination == null) throw new ArgumentNullException(nameof(Destination));

            Lzo1x.Decompress(Source, Destination);
        }

        #endregion ILzoCompression Implementation

    }

}
