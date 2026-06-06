using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;

namespace CorelDrawDesignFinder
{
    public static class CdrPreviewExtractor
    {
        /// <summary>
        /// Extracts the embedded preview image from a CorelDRAW (.cdr) file.
        /// Post-X4 (.cdr) files are standard ZIP containers.
        /// </summary>
        public static Bitmap ExtractPreview(string cdrFilePath)
        {
            if (string.IsNullOrEmpty(cdrFilePath) || !File.Exists(cdrFilePath))
                return null;

            try
            {
                using (FileStream fs = new FileStream(cdrFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // Check ZIP header 'PK\x03\x04' (0x50, 0x4B, 0x03, 0x04)
                    byte[] header = new byte[4];
                    if (fs.Read(header, 0, 4) == 4)
                    {
                        bool isZip = header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;
                        if (!isZip)
                        {
                            return null; // Classic older CDR format or unzippable
                        }
                    }
                    fs.Position = 0; // Reset position

                    using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Read))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            string lowerName = entry.FullName.ToLowerInvariant();
                            
                            // Check common preview image paths in CDR ZIP
                            if (lowerName.EndsWith("thumbnail.png") || 
                                lowerName.EndsWith("metadata/preview.png") ||
                                lowerName.EndsWith("previews/thumbnail.png") ||
                                lowerName.Contains("preview.png") ||
                                lowerName.Contains("thumbnail.png"))
                            {
                                using (Stream entryStream = entry.Open())
                                {
                                    // MemoryStream to avoid holding locks on zip files
                                    using (MemoryStream ms = new MemoryStream())
                                    {
                                        entryStream.CopyTo(ms);
                                        ms.Position = 0;
                                        return new Bitmap(ms);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error extracting preview from " + cdrFilePath + ": " + ex.Message);
            }
            return null;
        }
    }
}
