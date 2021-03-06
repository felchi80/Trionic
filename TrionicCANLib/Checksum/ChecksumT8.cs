﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using NLog;
using TrionicCANLib.Firmware;

namespace TrionicCANLib.Checksum
{
    public class ChecksumT8
    {
        private readonly static Logger logger = LogManager.GetCurrentClassLogger();

        public static ChecksumResult VerifyChecksum(string filename, bool autocorrect, ChecksumDelegate.ChecksumUpdate delegateShouldUpdate)
        {
            int checksumAreaOffset = GetChecksumAreaOffset(filename);
            if (checksumAreaOffset > FileT8.Length)
            {
                return ChecksumResult.InvalidFileLength;
            }

            logger.Debug("Checksum area offset: " + checksumAreaOffset.ToString("X8"));
            byte[] hash = CalculateLayer1ChecksumMD5(filename, checksumAreaOffset);
            // compare hash to bytes after checksum areaoffset
            byte[] layer1checksuminfile = FileTools.readdatafromfile(filename, checksumAreaOffset + 2, 16);
            if (!CompareByteArray(hash, layer1checksuminfile))
            {
                if (autocorrect)
                {
                    if (!FileTools.savedatatobinary(checksumAreaOffset + 2, 16, hash, filename))
                    {
                        return ChecksumResult.UpdateFailed;
                    }
                }
                else
                {
                    string filechecksum = string.Empty;
                    string realchecksum = string.Empty;
                    for (int i = 0; i < layer1checksuminfile.Length; i++)
                    {
                        filechecksum += layer1checksuminfile[i].ToString("X2") + " ";
                        realchecksum += hash[i].ToString("X2") + " ";
                    }
                    if(delegateShouldUpdate("Checksum validation Layer 1", filechecksum, realchecksum))
                    {
                        if (!FileTools.savedatatobinary(checksumAreaOffset + 2, 16, hash, filename))
                        {
                            return ChecksumResult.UpdateFailed;
                        }
                    }
                    else
                    {
                        return ChecksumResult.Layer1Failed;
                    }
                }
            }

            return CalculateLayer2Checksum(filename, checksumAreaOffset, autocorrect, delegateShouldUpdate);
        }

        static private int GetChecksumAreaOffset(string filename)
        {
            int retval = 0;
            if (filename == "") return retval;
            FileStream fsread = new FileStream(filename, FileMode.Open, FileAccess.Read);
            using (BinaryReader br = new BinaryReader(fsread))
            {
                fsread.Seek(0x20140, SeekOrigin.Begin);
                retval = (int)br.ReadByte() * 256 * 256 * 256;
                retval += (int)br.ReadByte() * 256 * 256;
                retval += (int)br.ReadByte() * 256;
                retval += (int)br.ReadByte();
            }
            fsread.Close();
            return retval;
        }

        static private byte[] CalculateLayer1ChecksumMD5(string filename, int OffsetLayer1)
        {
            /*
            1.	calculate checksum pointer
            2.	Checksum is 2 level based on Message Digest 5 algorithm
            3.	Pointer = @ 0x20140 and is a 4 byte pointer
            4.	Use MD5 to make 16 bytes digest from any string
            5.	name checksum pointer CHPTR
            6.	checksum area 1 ranges from 20000h to CHPTR – 20000h- 1
            7.	Create an MD5 hash from this string (20000h – (CHPTR – 20000h – 1))
                a.	MD5Init(Context)
                b.	MD5Update(Context, buffer, size)
                c.	MD5Final(Context, Md5Seed)
                d.	sMd5Seed = MD5Print(Md5Seed)
                e.	sMd5Seed = 16 bytes hex, so 32 bytes.
                f.	Now crypt sMd5Seed: xor every byte with 21h, then substract D6h (minus)
                g.	These 16 bytes are from CHPTR + 2 in the bin!!!! This is checksum level 1 !!
                         * */
            System.Security.Cryptography.MD5CryptoServiceProvider md5 = new System.Security.Cryptography.MD5CryptoServiceProvider();

            int len = OffsetLayer1 - 0x20000;//- 1;
            md5.Initialize();
            int end = 0x20000 + len;
            logger.Debug("Calculating from 0x20000 upto " + end.ToString("X8"));

            byte[] data = FileTools.readdatafromfile(filename, 0x20000, len);
            byte[] hash = md5.ComputeHash(data);
            byte[] finalhash = new byte[hash.Length];

            for (int i = 0; i < hash.Length; i++)
            {
                byte bcalc = hash[i];
                bcalc ^= 0x21;
                bcalc -= 0xD6;
                finalhash[i] = bcalc;
            }

            return finalhash;

        }

        static private bool CompareByteArray(byte[] arr1, byte[] arr2)
        {
            bool retval = true;
            if (arr1.Length != arr2.Length) retval = false;
            else
            {
                for (int t = 0; t < arr1.Length; t++)
                {
                    if (arr1[t] != arr2[t]) retval = false;
                }
            }
            return retval;
        }

        static private ChecksumResult CalculateLayer2Checksum(string filename, int OffsetLayer2, bool autocorrect, ChecksumDelegate.ChecksumUpdate delegateShouldUpdate)
        {
            ChecksumResult result = ChecksumResult.Layer2Failed;
            uint checksum0 = 0;
            uint checksum1 = 0;
            uint sum0 = 0;
            uint matrix_dimension = 0;
            uint partial_address = 0;
            uint x = 0;
            /*
            Get 0x100 byte buffer from CHPTR – CHPTR + 0xFF
            Because level 1 is in that area level1 must be correct first
            Prepare coded_buffer (0x100 buffer from chptr) with loop: coded_buffer(x) = (buffer (x) + 0xD6) xor 0x21 
            (add 0xd6 to every byte of buffer, then xor it by 0x21)
            [ 1 indexed, not 0 indexed ]
            So, 0x101 byte buffer with first byte ignored (convention)
                         * */
            byte[] coded_buffer = FileTools.readdatafromfile(filename, OffsetLayer2, 0x100);

            for (int i = 0; i < coded_buffer.Length; i++)
            {
                byte b = coded_buffer[i];
                b += 0xD6;
                b ^= 0x21;
                coded_buffer[i] = b;
            }

            byte[] complete_file = FileTools.readdatafromfile(filename, 0, 0x100000);
            int index = 0;
            bool chk_found = false;
            while (index < 0x100 && !chk_found)
            {
                if ((coded_buffer[index] == 0xFB) && (coded_buffer[index + 6] == 0xFC) && (coded_buffer[index + 0x0C] == 0xFD))
                {
                    sum0 = ((uint)coded_buffer[index + 1] * 0x01000000 + (uint)coded_buffer[index + 2] * 0x010000 + (uint)coded_buffer[index + 3] * 0x100 + (uint)coded_buffer[index + 4]);
                    matrix_dimension = (uint)coded_buffer[index + 7] * 0x01000000 + (uint)coded_buffer[index + 8] * 0x010000 + (uint)coded_buffer[index + 9] * 0x100 + (uint)coded_buffer[index + 10];
                    partial_address = (uint)coded_buffer[index + 0x0d] * 0x01000000 + (uint)coded_buffer[index + 0x0e] * 0x010000 + (uint)coded_buffer[index + 0x0F] * 0x100 + (uint)coded_buffer[index + 0x10];
                    if (matrix_dimension >= 0x020000)
                    {
                        checksum0 = 0;
                        x = partial_address;
                        while (x < (matrix_dimension - 4))
                        {
                            checksum0 = checksum0 + (uint)complete_file[x];
                            x++;
                        }
                        checksum0 = checksum0 + (uint)complete_file[matrix_dimension - 1];
                        checksum1 = 0;
                        x = partial_address;
                        while (x < (matrix_dimension - 4))
                        {
                            checksum1 = checksum1 + (uint)complete_file[x] * 0x01000000 + (uint)complete_file[x + 1] * 0x10000 + (uint)complete_file[x + 2] * 0x100 + (uint)complete_file[x + 3];
                            x = x + 4;
                        }
                        if ((checksum0 & 0xFFF00000) != (sum0 & 0xFFF00000))
                        {
                            checksum0 = checksum1;
                        }
                        if (checksum0 != sum0)
                        {
                            logger.Debug("Layer 2 checksum was invalid, should be updated!");
                            if (autocorrect)
                            {
                                result = UpdateLayer2(filename, OffsetLayer2, checksum0, index);
                            }
                            else
                            {
                                string filechecksum = sum0.ToString("X8");
                                string realchecksum = checksum0.ToString("X8");
                                if (delegateShouldUpdate("Checksum validation Layer 2", filechecksum, realchecksum))
                                {
                                    result = UpdateLayer2(filename, OffsetLayer2, checksum0, index);
                                }
                                else
                                {
                                    result = ChecksumResult.Layer2Failed;
                                }
                            }
                        }
                        else
                        {
                            result = ChecksumResult.Ok;
                        }
                        chk_found = true;
                    }
                }
                index++;
            }
            if (!chk_found)
            {
                logger.Debug("Layer 2 checksum could not be calculated [ file incompatible ]");
            }
            return result;
        }

        private static ChecksumResult UpdateLayer2(string filename, int OffsetLayer2, uint checksum0, int index)
        {
            ChecksumResult result;
            byte[] checksum_to_file = new byte[4];
            checksum_to_file[0] = Convert.ToByte((checksum0 >> 24) & 0x000000FF);
            checksum_to_file[1] = Convert.ToByte((checksum0 >> 16) & 0x000000FF);
            checksum_to_file[2] = Convert.ToByte((checksum0 >> 8) & 0x000000FF);
            checksum_to_file[3] = Convert.ToByte((checksum0) & 0x000000FF);
            checksum_to_file[0] = Convert.ToByte(((checksum_to_file[0] ^ 0x21) - (byte)0xD6) & 0x000000FF);
            checksum_to_file[1] = Convert.ToByte(((checksum_to_file[1] ^ 0x21) - (byte)0xD6) & 0x000000FF);
            checksum_to_file[2] = Convert.ToByte(((checksum_to_file[2] ^ 0x21) - (byte)0xD6) & 0x000000FF);
            checksum_to_file[3] = Convert.ToByte(((checksum_to_file[3] ^ 0x21) - (byte)0xD6) & 0x000000FF);
            if (FileTools.savedatatobinary(index + OffsetLayer2 + 1, 4, checksum_to_file, filename))
            {
                result = ChecksumResult.Ok;
            }
            else
            {
                result = ChecksumResult.UpdateFailed;
            }
            return result;
        }
    }
}
