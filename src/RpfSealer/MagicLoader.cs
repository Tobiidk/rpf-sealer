// MagicLoader — bootstraps GTA V NG decryption keys/tables from CodeWalker's
// encrypted "magic.dat" blob using an AES key extracted from the running game
// executable.
//
// Algorithm (clean-room reimplementation of CodeWalker's UseMagicData):
//   1. Seed System.Random with the Jenkins hash of the AES key.
//   2. Subtract four full-length pseudorandom byte streams from the blob.
//   3. AES-256-ECB (no padding, 1 round) decrypt with the AES key.
//   4. Deflate-decompress the result.
//   5. Split the plaintext into: NG keys | decrypt tables | PC LUT | AWC key.
//
// The magic.dat blob itself is distributed verbatim from CodeWalker's source
// tree under the MIT License. See README.md for full attribution.

using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using RageLib.GTA5.Cryptography;
using RageLib.GTA5.Cryptography.Helpers;
using RageLib.GTA5.Helpers;

namespace RpfSealer
{
    internal static class MagicLoader
    {
        // Plaintext layout after AES-decrypt + deflate:
        //   [0          , 27472         )  NG keys        (101 × 272)
        //   [27472      , 306000        )  decrypt tables (17 × 16 × 1024)
        //   [306000     , 306256        )  PC_LUT         (256)
        //   [306256     , 306272        )  AWC key        (16)
        private const int NgKeysBytes     = 27472;
        private const int NgTablesBytes   = 278528;
        private const int PcLutBytes      = 256;
        private const int AwcKeyBytes     = 16;

        /// <summary>
        /// Returns the magic blob bytes, preferring the assembly-embedded copy
        /// (single-file builds), falling back to Resources/magic.dat on disk
        /// (loose-layout builds). Null if neither source is available.
        /// </summary>
        public static byte[] LoadBlob()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("magic.dat"))
            {
                if (s != null) return ReadAll(s);
            }

            var exeDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            var diskPath = Path.Combine(exeDir, "Resources", "magic.dat");
            return File.Exists(diskPath) ? File.ReadAllBytes(diskPath) : null;
        }

        /// <summary>
        /// Populates <see cref="GTA5Constants"/> (NG keys, decrypt tables, PC_LUT,
        /// AES key) from the supplied magic blob using the supplied AES key, then
        /// derives the encrypt tables and encrypt LUTs. The derivation step is the
        /// heavy compute: a few seconds to a couple of minutes on current CPUs.
        /// </summary>
        /// <param name="progress">
        /// Called as (current, total, detail). There are 17 total steps: 3 encrypt
        /// table solves followed by 14 encrypt-LUT builds.
        /// </param>
        public static void Load(byte[] magicBlob, byte[] aesKey, Action<int, int, string> progress = null)
        {
            if (magicBlob == null) throw new ArgumentNullException(nameof(magicBlob));
            if (aesKey == null || aesKey.Length != 32)
                throw new ArgumentException("AES key must be 32 bytes (256 bits).", nameof(aesKey));

            byte[] plain = Unwrap(magicBlob, aesKey);

            int needed = NgKeysBytes + NgTablesBytes + PcLutBytes + AwcKeyBytes;
            if (plain.Length < needed)
                throw new InvalidDataException(
                    $"Magic unwrapped to {plain.Length} bytes, expected at least {needed}. " +
                    "Most likely the AES key is wrong for this blob.");

            int off = 0;

            var ngKeys = new byte[101][];
            for (int i = 0; i < 101; i++)
            {
                ngKeys[i] = new byte[272];
                Buffer.BlockCopy(plain, off + i * 272, ngKeys[i], 0, 272);
            }
            off += NgKeysBytes;

            var ngDecrypt = new uint[17][][];
            for (int i = 0; i < 17; i++)
            {
                ngDecrypt[i] = new uint[16][];
                for (int j = 0; j < 16; j++)
                {
                    ngDecrypt[i][j] = new uint[256];
                    Buffer.BlockCopy(plain, off + (i * 16 + j) * 1024,
                                     ngDecrypt[i][j], 0, 1024);
                }
            }
            off += NgTablesBytes;

            var pcLut = new byte[PcLutBytes];
            Buffer.BlockCopy(plain, off, pcLut, 0, PcLutBytes);
            // AWC key (16 bytes) follows; not used by RpfSealer.

            GTA5Constants.PC_AES_KEY           = (byte[])aesKey.Clone();
            GTA5Constants.PC_NG_KEYS           = ngKeys;
            GTA5Constants.PC_NG_DECRYPT_TABLES = ngDecrypt;
            GTA5Constants.PC_LUT               = pcLut;

            DeriveEncryptState(progress);
        }

        /// <summary>
        /// Write the six canonical .dat files that RageLib's
        /// <c>GTA5Constants.LoadFromPath</c> expects.
        /// </summary>
        public static void SaveDatFiles(string dir)
        {
            File.WriteAllBytes(Path.Combine(dir, "gtav_aes_key.dat"),                GTA5Constants.PC_AES_KEY);
            CryptoIO.WriteNgKeys(Path.Combine(dir, "gtav_ng_key.dat"),               GTA5Constants.PC_NG_KEYS);
            CryptoIO.WriteNgTables(Path.Combine(dir, "gtav_ng_decrypt_tables.dat"),  GTA5Constants.PC_NG_DECRYPT_TABLES);
            CryptoIO.WriteNgTables(Path.Combine(dir, "gtav_ng_encrypt_tables.dat"),  GTA5Constants.PC_NG_ENCRYPT_TABLES);
            CryptoIO.WriteLuts(Path.Combine(dir, "gtav_ng_encrypt_luts.dat"),        GTA5Constants.PC_NG_ENCRYPT_LUTs);
            File.WriteAllBytes(Path.Combine(dir, "gtav_hash_lut.dat"),               GTA5Constants.PC_LUT);
        }

        // ---------------------------------------------------------------------
        // internals
        // ---------------------------------------------------------------------

        private static byte[] Unwrap(byte[] magic, byte[] aesKey)
        {
            var rng = new Random(unchecked((int)JenkinsHash(aesKey)));

            int n = magic.Length;
            var rb1 = new byte[n]; rng.NextBytes(rb1);
            var rb2 = new byte[n]; rng.NextBytes(rb2);
            var rb3 = new byte[n]; rng.NextBytes(rb3);
            var rb4 = new byte[n]; rng.NextBytes(rb4);

            var cipher = new byte[n];
            for (int i = 0; i < n; i++)
                cipher[i] = (byte)((magic[i] - rb1[i] - rb2[i] - rb3[i] - rb4[i]) & 0xFF);

            byte[] plain = AesEcbDecryptNoPad(cipher, aesKey);

            using (var ms = new MemoryStream(plain))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                ds.CopyTo(outMs);
                return outMs.ToArray();
            }
        }

        private static byte[] AesEcbDecryptNoPad(byte[] data, byte[] key)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize   = 256;
                aes.BlockSize = 128;
                aes.Mode      = CipherMode.ECB;
                aes.Padding   = PaddingMode.None;
                aes.Key       = key;

                var buf = (byte[])data.Clone();
                int whole = data.Length - (data.Length % 16);
                if (whole > 0)
                    using (var dec = aes.CreateDecryptor())
                        dec.TransformBlock(buf, 0, whole, buf, 0);
                return buf;
            }
        }

        // Jenkins one-at-a-time over raw bytes (matches CodeWalker's JenkHash.GenHash(byte[])).
        private static uint JenkinsHash(byte[] data)
        {
            uint h = 0;
            for (int i = 0; i < data.Length; i++)
            {
                h += data[i];
                h += (h << 10);
                h ^= (h >> 6);
            }
            h += (h << 3);
            h ^= (h >> 11);
            h += (h << 15);
            return h;
        }

        private static void DeriveEncryptState(Action<int, int, string> progress)
        {
            var enc = new uint[17][][];
            var luts = new GTA5NGLUT[17][];
            for (int i = 0; i < 17; i++)
            {
                enc[i]  = new uint[16][];
                luts[i] = new GTA5NGLUT[16];
                for (int j = 0; j < 16; j++)
                {
                    enc[i][j]  = new uint[256];
                    luts[i][j] = new GTA5NGLUT();
                }
            }

            const int TotalSteps = 17;
            int step = 0;

            progress?.Invoke(++step, TotalSteps, "solving encrypt table 1");
            enc[0]  = RandomGauss.Solve(GTA5Constants.PC_NG_DECRYPT_TABLES[0]);
            progress?.Invoke(++step, TotalSteps, "solving encrypt table 2");
            enc[1]  = RandomGauss.Solve(GTA5Constants.PC_NG_DECRYPT_TABLES[1]);
            progress?.Invoke(++step, TotalSteps, "solving encrypt table 17");
            enc[16] = RandomGauss.Solve(GTA5Constants.PC_NG_DECRYPT_TABLES[16]);
            GTA5Constants.PC_NG_ENCRYPT_TABLES = enc;

            for (int k = 2; k <= 15; k++)
            {
                progress?.Invoke(++step, TotalSteps, $"building encrypt LUT {k + 1}");
                luts[k] = LookUpTableGenerator.BuildLUTs2(GTA5Constants.PC_NG_DECRYPT_TABLES[k]);
            }
            GTA5Constants.PC_NG_ENCRYPT_LUTs = luts;
        }

        private static byte[] ReadAll(Stream s)
        {
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
