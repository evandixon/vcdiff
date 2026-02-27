using SharpCompress.Compressors.Xz;
using System;
using System.IO;
using VCDiff.Shared;

namespace VCDiff.Compressors
{
    public class XzCompressor : ICompressor, IDisposable
    {
        public XzCompressor()
        {
            addRunCompressedBuffer = new();
            instructionsCompressedBuffer = new();
            addressesCompressedBuffer = new();

            addRunDecompressor = new(addRunCompressedBuffer);
            instructionsDecompressor = new(instructionsCompressedBuffer);
            addressesDecompressor = new(addressesCompressedBuffer);

            minimumCompressionInput = 10;
            minimumCompressionSavings = 2;
        }

        private readonly MemoryStream addRunCompressedBuffer;
        private readonly MemoryStream instructionsCompressedBuffer;
        private readonly MemoryStream addressesCompressedBuffer;
        private readonly XZStream addRunDecompressor;
        private readonly XZStream instructionsDecompressor;
        private readonly XZStream addressesDecompressor;
        private readonly int minimumCompressionInput;
        private readonly int minimumCompressionSavings;

        public byte CompressorId => 2;

        public PinnedArrayRental Decompress(WindowSectionType windowSectionType, PinnedArrayRental sectionData)
        {
            if (sectionData.Data == null)
            {
                throw new ArgumentException("Cannot decompress null data");
            }

            var (memoryStream, xzStream) = GetStreams(windowSectionType);

            var uncompressedLength = VarIntBE.ParseInt32(sectionData.AsSpan(), out int uncompressedLengthByteCount);
            var compressedData = sectionData.AsSpan().Slice(uncompressedLengthByteCount);

            // Each section in a window uses the same compression stream throughout the file
            // If this is not the first window, reuse the same stream from before, just using different data
            memoryStream.SetLength(compressedData.Length);
            memoryStream.Position = 0;
            memoryStream.Write(compressedData);
            memoryStream.Position = 0;

            var decompressedData = new PinnedArrayRental(uncompressedLength);
            xzStream.ReadExactly(decompressedData.AsSpan());

            return decompressedData;
        }

        public MemoryStream? Compress(WindowSectionType windowSectionType, MemoryStream uncompressedStream)
        {
            if (uncompressedStream.Length < minimumCompressionInput)
            {
                return null;
            }

            // xdelta does a trial compression to see if it's worth compressing
            // It keeps the compressed content if it saves 2 bytes,
            // otherwise it keeps the uncompressed content and seems to rewind the compression stream (I'm unsure of the specifics)
            // However, we live in a different world than the land of C
            // To maintain compatibility with xdelta, we must have decompression use a single decompression stream,
            // and consequently, we must use the same compression stream.
            // This means we cannot simply throw away compressed content,
            // or we'll screw with the internal state of the compression stream and possibly break decompression.

            // There may be future optimizations that can be done, but for now I'm optimizing for correctness of output

            using (var trialMemoryStream = new MemoryStream())
            {
                using (var trialCompressionStream = new XZStream(trialMemoryStream))
                {
                    uncompressedStream.CopyTo(trialCompressionStream);
                }

                if (trialMemoryStream.Length < (uncompressedStream.Length - minimumCompressionSavings))
                {
                    // It's more efficient to not compress
                    return null;
                }
            }

            var (memoryStream, xzStream) = GetStreams(windowSectionType);
            memoryStream.SetLength(0);
            uncompressedStream.CopyTo(xzStream);

            var compressedStream = new MemoryStream();
            VarIntBE.AppendInt32((int)uncompressedStream.Length, compressedStream);
            memoryStream.CopyTo(compressedStream);
            compressedStream.Position = 0;
            return compressedStream;
        }

        private (MemoryStream, XZStream) GetStreams(WindowSectionType windowSectionType)
        {
            switch (windowSectionType)
            {
                case WindowSectionType.AddRunData:
                    return (addRunCompressedBuffer, addRunDecompressor);
                case WindowSectionType.InstructionsAndSizes:
                    return (instructionsCompressedBuffer, instructionsDecompressor);
                case WindowSectionType.AddressForCopy:
                    return (addressesCompressedBuffer, addressesDecompressor);
                default:
                    throw new ArgumentOutOfRangeException(nameof(windowSectionType));
            }
        }

        public void Dispose()
        {
            addressesCompressedBuffer?.Dispose();
            instructionsCompressedBuffer?.Dispose();
            addressesCompressedBuffer?.Dispose();
            addRunDecompressor?.Dispose();
            instructionsDecompressor?.Dispose();
            addressesDecompressor?.Dispose();
        }
    }
}
