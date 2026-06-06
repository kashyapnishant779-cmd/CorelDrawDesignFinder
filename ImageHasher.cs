using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CorelDrawDesignFinder
{
    public static class ImageHasher
    {
        /// <summary>
        /// Computes an 8x8 Average Image Hash (aHash) for a given bitmap.
        /// Returns a 64-bit unsigned integer (ulong) representing the image state.
        /// </summary>
        public static ulong ComputeAverageHash(Bitmap original)
        {
            if (original == null)
                return 0;

            using (Bitmap small = ResizeImage(original, 8, 8))
            {
                // Calculate average intensity
                double sum = 0;
                double[] grayscale = new double[64];

                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        // Standard luma formula: 0.299 * R + 0.587 * G + 0.114 * B
                        double gray = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                        grayscale[y * 8 + x] = gray;
                        sum += gray;
                    }
                }

                double average = sum / 64.0;

                // Build hash bit-by-bit
                ulong hash = 0;
                for (int i = 0; i < 64; i++)
                {
                    if (grayscale[i] >= average)
                    {
                        hash |= (1UL << i);
                    }
                }
                return hash;
            }
        }

        /// <summary>
        /// Calculates the Hamming Distance (how many bits are different) between two 64-bit hashes.
        /// Returns a value between 0 (identical) and 64 (totally different).
        /// </summary>
        public static int CalculateHammingDistance(ulong hash1, ulong hash2)
        {
            ulong diff = hash1 ^ hash2;
            
            // Count set bits (Brian Kernighan's bit counting algorithm)
            int count = 0;
            while (diff > 0)
            {
                count++;
                diff &= (diff - 1);
            }
            return count;
        }

        /// <summary>
        /// Resizes a bitmap with high quality.
        /// </summary>
        private static Bitmap ResizeImage(Bitmap image, int width, int height)
        {
            Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (ImageAttributes wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }
            return result;
        }
    }
}
