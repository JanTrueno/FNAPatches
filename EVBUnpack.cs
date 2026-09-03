// Enigma Virtual Box unpacker.
//
// Some Windows games ship as a single Enigma Virtual Box bundle: the real executable and
// its whole data tree are packed into one .exe with a virtual filesystem appended. Ports
// need those files on disk, and asking players to unpack on a PC first is a poor step, so
// this runs on-device from a port's patchscript.
//
// Only the virtual filesystem is extracted; the outer Enigma launcher PE is not rebuilt,
// since the real (managed) executable inside is what actually gets run.
//
// Deliberately plain C# -- no local functions, index-from-end or pattern matching -- so it
// builds with mcs and runs on the Mono runtime the ports bundle.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EvbUnpack
{
    internal class Node
    {
        public byte Type;
        public string Name = "";
        public uint ObjectsCount;
        public uint OriginalSize;
        public uint StoredSize;
        public long Offset;
    }

    internal class HeaderNode
    {
        public uint Size;
        public uint ObjectsCount;
    }

    internal class NamedNode
    {
        public string Name = "";
        public byte Type;
    }

    internal class OptionalFileNode
    {
        public uint OriginalSize;
        public uint StoredSize;
    }

    internal static class Program
    {
        private const byte NodeTypeMain = 0;
        private const byte NodeTypeFile = 2;
        private const byte NodeTypeFolder = 3;
        private static readonly byte[] EvbMagic = { (byte)'E', (byte)'V', (byte)'B', 0 };

        // 2s pad, uint original_size, 4s pad, 3x 8s filetimes, 7s pad, uint stored_size, 4s pad
        private const int LegacyOptionalFileNodeSize = 49;

        private static int Main(string[] args)
        {
            bool legacyFs = false;
            bool listOnly = false;
            List<string> positional = new List<string>();

            foreach (string a in args)
            {
                if (a == "--legacy-fs") legacyFs = true;
                else if (a == "--list" || a == "-l") listOnly = true;
                else positional.Add(a);
            }

            if (positional.Count < 2)
            {
                Console.Error.WriteLine("Enigma Virtual Box unpacker");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Usage: mono EVBUnpack.exe <input.exe> <output_dir> [--legacy-fs] [--list]");
                Console.Error.WriteLine("  --legacy-fs  older bundles interleave the file table with the file data");
                Console.Error.WriteLine("  --list       print the virtual filesystem without extracting");
                return 1;
            }

            string inputPath = positional[0];
            string outDir = positional[1];

            using (FileStream fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                long magicOffset = FindMagic(fs);
                if (magicOffset < 0)
                {
                    Console.Error.WriteLine("EVB filesystem magic not found -- this file is probably not an Enigma Virtual Box bundle.");
                    return 1;
                }

                fs.Seek(magicOffset, SeekOrigin.Begin);
                List<Node> nodes;
                using (BinaryReader br = new BinaryReader(fs, Encoding.UTF8, true))
                {
                    nodes = legacyFs ? ParseLegacyTree(br) : ParseModernTree(br);
                }

                if (nodes.Count == 0)
                {
                    Console.Error.WriteLine("The file table is empty or corrupt. Try toggling --legacy-fs.");
                    return 1;
                }

                if (!listOnly) Directory.CreateDirectory(outDir);

                int idx = 0;
                Node mainNode = nodes[idx++];
                Console.WriteLine("Filesystem:");
                for (int i = 0; i < mainNode.ObjectsCount; i++)
                {
                    idx = Traverse(fs, nodes, idx, outDir, listOnly);
                }

                Console.WriteLine(listOnly ? "Listing complete." : "Extraction complete.");
                return 0;
            }
        }

        private static int Traverse(FileStream fs, List<Node> nodes, int idx, string parentDir, bool listOnly)
        {
            Node node = nodes[idx++];
            string name = (node.Type == NodeTypeFolder && node.Name == "%DEFAULT FOLDER%") ? "" : node.Name;

            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0 || name.IndexOf(':') >= 0 || name == "." || name == "..")
                throw new InvalidDataException("Invalid node name in file table: '" + name + "'");

            string path = string.IsNullOrEmpty(name) ? parentDir : Path.Combine(parentDir, name);
            Console.WriteLine("   " + path);

            if (node.Type == NodeTypeFile)
            {
                if (!listOnly) ExtractFile(fs, path, node);
            }
            else if (node.Type == NodeTypeFolder)
            {
                if (!listOnly) Directory.CreateDirectory(path);
                for (int i = 0; i < node.ObjectsCount; i++)
                    idx = Traverse(fs, nodes, idx, path, listOnly);
            }

            return idx;
        }

        // Streamed so a 600MB bundle never has to be read into memory at once.
        private static long FindMagic(FileStream fs)
        {
            const int bufSize = 1 << 20;
            byte[] buffer = new byte[bufSize + EvbMagic.Length - 1];
            long baseOffset = 0;
            int carry = 0;
            fs.Seek(0, SeekOrigin.Begin);

            while (true)
            {
                int read = fs.Read(buffer, carry, bufSize);
                if (read <= 0) break;
                int total = carry + read;

                int found = IndexOf(buffer, total, EvbMagic);
                if (found >= 0) return baseOffset + found;

                int keep = EvbMagic.Length - 1;
                if (total > keep)
                {
                    Array.Copy(buffer, total - keep, buffer, 0, keep);
                    baseOffset += total - keep;
                    carry = keep;
                }
                else carry = total;
            }
            return -1;
        }

        private static int IndexOf(byte[] haystack, int haystackLen, byte[] needle)
        {
            for (int i = 0; i <= haystackLen - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        private static HeaderNode ReadHeaderNode(BinaryReader br)
        {
            HeaderNode h = new HeaderNode();
            h.Size = br.ReadUInt32();
            br.ReadBytes(8);
            h.ObjectsCount = br.ReadUInt32();
            return h;
        }

        private static NamedNode ReadNamedNode(BinaryReader br)
        {
            List<byte> nameBytes = new List<byte>();
            while (true)
            {
                byte b0 = br.ReadByte();
                byte b1 = br.ReadByte();
                if (b0 == 0 && b1 == 0) break;
                nameBytes.Add(b0);
                nameBytes.Add(b1);
            }
            NamedNode n = new NamedNode();
            n.Type = br.ReadByte();
            n.Name = Encoding.Unicode.GetString(nameBytes.ToArray());
            return n;
        }

        private static OptionalFileNode ReadOptionalLegacyPeFileNode(BinaryReader br)
        {
            br.ReadBytes(2);
            uint originalSize = br.ReadUInt32();
            br.ReadBytes(4);
            br.ReadBytes(8); br.ReadBytes(8); br.ReadBytes(8);
            br.ReadBytes(7);
            uint storedSize = br.ReadUInt32();
            br.ReadBytes(4);
            OptionalFileNode o = new OptionalFileNode();
            o.OriginalSize = originalSize;
            o.StoredSize = storedSize;
            return o;
        }

        private static OptionalFileNode ReadOptionalFileNode(BinaryReader br)
        {
            br.ReadBytes(2);
            uint originalSize = br.ReadUInt32();
            br.ReadBytes(4);
            br.ReadBytes(8); br.ReadBytes(8); br.ReadBytes(8);
            br.ReadBytes(15);
            uint storedSize = br.ReadUInt32();
            OptionalFileNode o = new OptionalFileNode();
            o.OriginalSize = originalSize;
            o.StoredSize = storedSize;
            return o;
        }

        // Older bundles put the file table and the file data together, so parsing has to
        // seek around each node.
        private static List<Node> ParseLegacyTree(BinaryReader br)
        {
            List<Node> nodes = new List<Node>();
            byte[] sig = br.ReadBytes(4);
            br.ReadBytes(60);
            if (!SpanEquals(sig, EvbMagic)) throw new InvalidDataException("Invalid EVB signature");

            Stream stream = br.BaseStream;
            long maxObjectCount = 0;
            long currentObjectCount = 0;

            while (true)
            {
                long seekOrigin = stream.Position;
                HeaderNode header;
                NamedNode named;
                try
                {
                    header = ReadHeaderNode(br);
                    named = ReadNamedNode(br);
                }
                catch (EndOfStreamException) { break; }

                Node node = new Node();
                node.Type = named.Type;
                node.Name = named.Name;
                node.ObjectsCount = header.ObjectsCount;

                if (named.Type == NodeTypeFile)
                {
                    stream.Seek(seekOrigin + header.Size + 4 - LegacyOptionalFileNodeSize, SeekOrigin.Begin);
                    OptionalFileNode opt = ReadOptionalLegacyPeFileNode(br);
                    node.OriginalSize = opt.OriginalSize;
                    node.StoredSize = opt.StoredSize;
                    node.Offset = stream.Position;
                    stream.Seek(opt.StoredSize, SeekOrigin.Current);
                    currentObjectCount++;
                }
                else if (named.Type == NodeTypeFolder)
                {
                    stream.Seek(seekOrigin + header.Size + 4, SeekOrigin.Begin);
                    maxObjectCount += header.ObjectsCount;
                    currentObjectCount++;
                }
                else if (named.Type == NodeTypeMain)
                {
                    stream.Seek(seekOrigin + header.Size + 4, SeekOrigin.Begin);
                }
                else break;

                nodes.Add(node);
                if (currentObjectCount > maxObjectCount && maxObjectCount > 0) break;
            }

            return nodes;
        }

        // Newer bundles keep the file table separate from one contiguous data blob.
        private static List<Node> ParseModernTree(BinaryReader br)
        {
            List<Node> nodes = new List<Node>();
            byte[] sig = br.ReadBytes(4);
            br.ReadBytes(60);
            if (!SpanEquals(sig, EvbMagic)) throw new InvalidDataException("Invalid EVB signature");

            Stream stream = br.BaseStream;
            HeaderNode mainHeader = ReadHeaderNode(br);
            long absOffset = stream.Position + mainHeader.Size - 12;
            stream.Seek(-1, SeekOrigin.Current);

            Node mainNode = new Node();
            mainNode.Type = NodeTypeMain;
            mainNode.ObjectsCount = mainHeader.ObjectsCount;
            nodes.Add(mainNode);

            long maxObjectCount = 0;
            long currentObjectCount = 0;

            while (true)
            {
                HeaderNode header;
                NamedNode named;
                try
                {
                    header = ReadHeaderNode(br);
                    named = ReadNamedNode(br);
                }
                catch (EndOfStreamException) { break; }

                Node node = new Node();
                node.Type = named.Type;
                node.Name = named.Name;
                node.ObjectsCount = header.ObjectsCount;

                if (named.Type == NodeTypeFile)
                {
                    OptionalFileNode opt = ReadOptionalFileNode(br);
                    node.OriginalSize = opt.OriginalSize;
                    node.StoredSize = opt.StoredSize;
                    node.Offset = absOffset;
                    absOffset += opt.StoredSize;
                    currentObjectCount++;
                }
                else if (named.Type == NodeTypeFolder)
                {
                    stream.Seek(25, SeekOrigin.Current);
                    maxObjectCount += header.ObjectsCount;
                    currentObjectCount++;
                }
                else break;

                nodes.Add(node);
                if (currentObjectCount > maxObjectCount && maxObjectCount > 0) break;
            }

            return nodes;
        }

        private static bool SpanEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void ExtractFile(FileStream fs, string path, Node node)
        {
            fs.Seek(node.Offset, SeekOrigin.Begin);
            using (FileStream outFs = File.Create(path))
            {
                if (node.OriginalSize == node.StoredSize)
                {
                    CopyBytes(fs, outFs, node.StoredSize);
                    return;
                }

                // Compressed entry: a table of aPLib-compressed chunk sizes precedes the data.
                BinaryReader br = new BinaryReader(fs, Encoding.UTF8, true);
                uint chunkBlockSize = br.ReadUInt32();
                br.ReadBytes(4);
                byte[] chunkTable = br.ReadBytes((int)chunkBlockSize - 8);

                // The table comes in 12-byte rows (chunk size, total size, padding), and the
                // final row is short, so taking every third uint is what reliably yields sizes.
                List<uint> chunkSizes = new List<uint>();
                for (int i = 0; i + 4 <= chunkTable.Length; i += 12)
                    chunkSizes.Add(BitConverter.ToUInt32(chunkTable, i));

                long remaining = node.StoredSize - chunkBlockSize;
                long written = 0;
                int chunkIndex = 0;

                while (remaining > 0)
                {
                    uint chunkSize = chunkIndex < chunkSizes.Count ? chunkSizes[chunkIndex] : (uint)remaining;
                    chunkIndex++;
                    int toRead = (int)Math.Min(chunkSize, remaining);
                    byte[] compressed = br.ReadBytes(toRead);
                    remaining -= toRead;
                    byte[] plain = APLib.Decompress(compressed);
                    outFs.Write(plain, 0, plain.Length);
                    written += plain.Length;
                }

                if (written != node.OriginalSize)
                    Console.Error.WriteLine("Warning: size mismatch for " + path + ": expected " + node.OriginalSize + ", got " + written);
            }
        }

        private static void CopyBytes(FileStream src, FileStream dst, long size)
        {
            byte[] buffer = new byte[1 << 16];
            long remaining = size;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = src.Read(buffer, 0, toRead);
                if (read <= 0) throw new EndOfStreamException("Unexpected end of file while extracting.");
                dst.Write(buffer, 0, read);
                remaining -= read;
            }
        }
    }

    // aPLib depacker (http://ibsensoftware.com/), for bundles that store files compressed.
    internal static class APLib
    {
        public static byte[] Decompress(byte[] data)
        {
            if (data.Length >= 24 && data[0] == 'A' && data[1] == 'P' && data[2] == '3' && data[3] == '2')
            {
                uint headerSize = BitConverter.ToUInt32(data, 4);
                uint packedSize = BitConverter.ToUInt32(data, 8);
                byte[] slice = new byte[packedSize];
                Array.Copy(data, headerSize, slice, 0, packedSize);
                data = slice;
            }
            return new Depacker(data).Run();
        }

        private class Depacker
        {
            private readonly byte[] _source;
            private readonly List<byte> _dest = new List<byte>();
            private int _srcPos;
            private int _tag;
            private int _bitCount;

            public Depacker(byte[] source) { _source = source; }

            private int GetBit()
            {
                _bitCount--;
                if (_bitCount < 0)
                {
                    _tag = _source[_srcPos++];
                    _bitCount = 7;
                }
                int bit = (_tag >> 7) & 1;
                _tag <<= 1;
                return bit;
            }

            private int GetGamma()
            {
                int result = 1;
                while (true)
                {
                    result = (result << 1) + GetBit();
                    if (GetBit() == 0) break;
                }
                return result;
            }

            public byte[] Run()
            {
                int r0 = -1;
                int lwm = 0;
                bool done = false;

                _dest.Add(_source[_srcPos++]);

                while (!done)
                {
                    if (GetBit() == 1)
                    {
                        if (GetBit() == 1)
                        {
                            if (GetBit() == 1)
                            {
                                int offs = 0;
                                for (int i = 0; i < 4; i++) offs = (offs << 1) + GetBit();
                                _dest.Add(offs != 0 ? _dest[_dest.Count - offs] : (byte)0);
                                lwm = 0;
                            }
                            else
                            {
                                int offsByte = _source[_srcPos++];
                                int length = 2 + (offsByte & 1);
                                int offs = offsByte >> 1;
                                if (offs != 0)
                                {
                                    for (int i = 0; i < length; i++) _dest.Add(_dest[_dest.Count - offs]);
                                }
                                else done = true;
                                r0 = offs;
                                lwm = 1;
                            }
                        }
                        else
                        {
                            int offs = GetGamma();
                            if (lwm == 0 && offs == 2)
                            {
                                offs = r0;
                                int length = GetGamma();
                                for (int i = 0; i < length; i++) _dest.Add(_dest[_dest.Count - offs]);
                            }
                            else
                            {
                                offs -= (lwm == 0) ? 3 : 2;
                                offs <<= 8;
                                offs += _source[_srcPos++];
                                int length = GetGamma();
                                if (offs >= 32000) length++;
                                if (offs >= 1280) length++;
                                if (offs < 128) length += 2;
                                for (int i = 0; i < length; i++) _dest.Add(_dest[_dest.Count - offs]);
                                r0 = offs;
                            }
                            lwm = 1;
                        }
                    }
                    else
                    {
                        _dest.Add(_source[_srcPos++]);
                        lwm = 0;
                    }
                }

                return _dest.ToArray();
            }
        }
    }
}
